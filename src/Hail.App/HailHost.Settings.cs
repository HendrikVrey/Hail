using System.IO;
using System.Security;
using Hail.Core.Hosting;
using Hail.Core.Settings;
using Hail.Core.Updates;
using Hail.Plugins;
using Hail.Providers.Apps;
using Hail.Providers.Calculator;
using Hail.Providers.Commands;
using Hail.Providers.Files;
using Hail.Providers.Web;
using Hail.Windows.Hotkeys;
using Hail.Windows.Startup;

namespace Hail.App;

/// <summary>What clicking the last notification does; only the last one shown counts.</summary>
internal enum NoticeAction
{
    None,
    AskAboutPlugins,
    OpenGeneral,
    OpenUpdates,
}

/// <summary>
/// The settings window's side of the host (Hail.md §10.4): each change is saved and put in force
/// at once, with no restart. The shortcut is re-registered, the web search is rebuilt when its
/// engines change, and a provider switched on or off changes the set in force.
/// </summary>
internal sealed partial class HailHost : ISettingsHost
{
    private const string ProjectPage = "https://github.com/HendrikVrey/Hail";

    private SettingsWindow? _settingsWindow;

    public event Action? PluginsChanged;

    public HailSettings Settings => _settings ?? HailSettings.Default;

    string ISettingsHost.Version => Version();

    public bool HotkeyRegistered => _hotkeyRegistered;

    public IReadOnlyList<BuiltInProvider> BuiltInProviders =>
    [
        new(AppsProvider.ProviderId, "Apps", "Everything the Start menu lists, desktop and Microsoft Store apps alike.", []),
        new(FilesProvider.ProviderId, "Files", "Files by name from the Windows Search index, and any folder whose path you type.", []),
        new(CalculatorProvider.ProviderId, "Calculator", "Sums as you type: 15% of 240, 2^10, sqrt(2).", [CalculatorProvider.Keyword]),
        new(CommandsProvider.ProviderId, "Commands", "Lock, Sleep, Sign out, Restart and Shut down, and Hail's own commands.", []),
        new(WebSearchProvider.ProviderId, "Web search", "Sends a search to your browser when you press Enter on one, and offers the last row of every search.", [.. Settings.WebSearch.Engines.Select(e => e.Keyword)]),
    ];

    public IReadOnlyDictionary<string, string> KeywordsOutsideWebSearch
    {
        get
        {
            var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var provider in _builtIns.Where(p => p.Id != WebSearchProvider.ProviderId).Concat(_pluginRegistrations))
            {
                foreach (var keyword in provider.Keywords)
                {
                    taken.TryAdd(keyword.Keyword, provider.Name);
                }
            }

            return taken;
        }
    }

    public IReadOnlyList<PluginEntry> Plugins => _plugins?.Entries ?? [];

    public bool CanRecordPluginAnswers => _plugins?.CanRecordAnswers ?? false;

    public int HistoryCount => _history?.Count ?? 0;

    public UpdateCoordinator Updates => _updates ?? throw new InvalidOperationException("The updater is made when Hail starts.");

    public StartupState StartupState =>
        Environment.ProcessPath is { } executable ? _startup.StateFor(executable) : StartupState.Off;

    public string? Apply(HailSettings next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var previous = Settings;

        try
        {
            _settingsStore.Save(next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError("Saving the settings failed.", ex);
            return $"Hail could not save its settings ({ex.GetType().Name}), so nothing was changed. The log has the reason.";
        }

        _settings = next;
        _box?.SetKeepLastQuery(next.KeepLastQuery);

        var webChanged = !SameEngines(previous.WebSearch, next.WebSearch);
        if (webChanged)
        {
            var index = _builtIns.FindIndex(p => p.Id == WebSearchProvider.ProviderId);
            _builtIns[index] = WebRegistration(next.WebSearch);
        }

        if (webChanged || !previous.DisabledProviders.SetEquals(next.DisabledProviders))
        {
            _supervisor!.Replace(Compose(_pluginRegistrations));
            _model!.ProvidersReplaced();
            _box!.Refresh();
        }

        if (previous.CheckForUpdates != next.CheckForUpdates && _updates is not null)
        {
            _updates.Allowed = next.CheckForUpdates;
            CheckForUpdatesIfDue();
        }

        log.LogInfo("Settings changed in the settings window.");
        return null;
    }

    public void SuspendHotkey()
    {
        if (_hotkeyRegistered && _host is not null)
        {
            GlobalHotkey.Unregister(_host.Handle, HostWindow.HotkeyId);
            _hotkeyRegistered = false;
            log.LogInfo($"{_chord.Display} let go while a new shortcut is recorded.");
        }
    }

    public string? TryHotkey(Chord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        if (chord.Problem() is { } refused)
        {
            return char.ToUpperInvariant(refused[0]) + refused[1..] + ".";
        }

        SuspendHotkey();
        var error = GlobalHotkey.Register(_host!.Handle, HostWindow.HotkeyId, chord);
        if (error != 0)
        {
            log.LogInfo($"{chord.Display} could not be registered (Win32 error {error}).");
            return GlobalHotkey.Describe(chord, error);
        }

        if (chord != Settings.Hotkey && Apply(Settings with { Hotkey = chord }) is { } problem)
        {
            // Not kept unless saved: a shortcut that works until the next start would surprise.
            GlobalHotkey.Unregister(_host.Handle, HostWindow.HotkeyId);
            return problem;
        }

        _chord = chord;
        _hotkeyRegistered = true;
        _tray?.SetTip(TrayTip());
        log.LogInfo($"{chord.Display} registered.");
        return null;
    }

    public void ResumeHotkey()
    {
        if (!_hotkeyRegistered && !_disposed)
        {
            RegisterHotkey();
            _tray?.SetTip(TrayTip());
        }
    }

    public string? SetStartup(bool enabled)
    {
        if (Environment.ProcessPath is not { } executable)
        {
            return "Hail cannot tell where it is running from, so it cannot set itself to start.";
        }

        try
        {
            if (enabled)
            {
                _startup.Enable(executable);
            }
            else
            {
                _startup.Disable();
            }

            log.LogInfo($"Start at sign-in switched {(enabled ? "on" : "off")}.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            log.LogError("Changing start at sign-in failed.", ex);
            return "Windows would not let Hail change this. The log has the reason.";
        }
    }

    public string DescribePlugin(PluginEntry entry) => StatusLine(entry);

    public void AskAboutPlugin(PluginEntry entry) => AskAbout(entry);

    public void SwitchOffPlugin(PluginEntry entry) => Answer(entry, enabled: false);

    public void EditPluginSettings(PluginEntry entry) => EditSettings(entry);

    public void OpenPluginsFolder() => _ = OpenPluginsFolderAsync();

    public void ClearHistory()
    {
        _history?.Clear();
        log.LogInfo("History cleared in the settings window.");
    }

    public void OpenDataFolder() => _ = OpenAsync(() => _launcher!.OpenPathAsync(paths.Root, _lifetime.Token), "Hail's folder");

    public void OpenReleasesPage() => _ = OpenAsync(() => _launcher!.OpenUriAsync(new Uri(ProjectPage + "/releases"), _lifetime.Token), "the releases page");

    public void OpenProjectPage() => _ = OpenAsync(() => _launcher!.OpenUriAsync(new Uri(ProjectPage), _lifetime.Token), "the project page");

    /// <summary>Opens the settings window on <paramref name="section"/>, or brings the open one forward there.</summary>
    private void OpenSettings(SettingsSection section)
    {
        if (_disposed)
        {
            return;
        }

        if (_settingsWindow is { } open)
        {
            open.ShowSection(section);
            if (open.WindowState == System.Windows.WindowState.Minimized)
            {
                open.WindowState = System.Windows.WindowState.Normal;
            }

            Present(open);
            return;
        }

        var window = new SettingsWindow(this, section);
        _settingsWindow = window;
        window.Closed += (_, _) => _settingsWindow = null;
        Present(window);
    }

    private async Task OpenAsync(Func<ValueTask> open, string what)
    {
        try
        {
            await open().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Opening a folder or a page failed; said in the log and the tray, and Hail carries on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.LogError($"Opening {what} failed.", ex);
            Tell("Hail", $"Hail could not open {what}. The log has the reason.");
        }
    }

    private static bool SameEngines(Core.Ports.WebSearchOptions left, Core.Ports.WebSearchOptions right) =>
        string.Equals(left.DefaultKeyword, right.DefaultKeyword, StringComparison.Ordinal)
        && left.Engines.SequenceEqual(right.Engines);
}
