using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Hail.Core.Hosting;
using Hail.Core.Plugins;
using Hail.Persistence;
using Hail.Plugins;
using Hail.Windows.Security;
using Hail.Windows.Windowing;

namespace Hail.App;

/// <summary>
/// Plugins, from the host's side (Hail.md §6.3, §9): reading the plugins folder, putting the
/// enabled ones in force, asking about new ones, drawing their settings, and unloading the old
/// set on Reload plugins.
/// </summary>
internal sealed partial class HailHost
{
    /// <summary>How long a plugin is given to stop its threads when it is unloaded or Hail quits.</summary>
    private static readonly TimeSpan PluginDisposeBudget = TimeSpan.FromSeconds(3);

    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PluginSettingsWindow> _settingsWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DpapiSecretProtector _protector = new();

    private PluginManager? _plugins;
    private PluginConsentWindow? _consent;

    /// <summary>What clicking the last notification does.</summary>
    private NoticeAction _noticeAction;
    private bool _reloading;
    private bool _reloadAgain;

    /// <summary>The box's "Reload plugins": after the action that asked has finished and the box has hidden.</summary>
    public void ReloadPlugins() =>
        Application.Current.Dispatcher.BeginInvoke(
            () => _ = ReloadPluginsAsync("the box"),
            System.Windows.Threading.DispatcherPriority.Background);

    private PluginManager CreatePluginManager()
    {
        var approvals = new PluginApprovalStore(paths);
        if (approvals.Load() is { } approvalProblem)
        {
            log.LogError(approvalProblem);
        }

        var settings = new PluginSettingsStore(paths);
        if (settings.Load() is { } settingsProblem)
        {
            log.LogError(settingsProblem);
        }

        return new PluginManager(paths, approvals, settings, _protector, log);
    }

    /// <summary>
    /// Reads the plugins folder and puts what it finds in force, then unloads the set it
    /// replaced. On the UI thread; the reading and the unloading happen off it. A reload asked
    /// for while one is running runs once more when it ends.
    /// </summary>
    private async Task ReloadPluginsAsync(string reason)
    {
        if (_reloading)
        {
            _reloadAgain = true;
            return;
        }

        _reloading = true;
        try
        {
            do
            {
                _reloadAgain = false;
                await ReloadOnceAsync(reason).ConfigureAwait(true);
                reason = "a reload asked for during the last";
            }
            while (_reloadAgain && !_disposed);
        }
        finally
        {
            _reloading = false;
        }
    }

    private async Task ReloadOnceAsync(string reason)
    {
        var plugins = _plugins!;
        PluginScan scan;
        try
        {
            scan = await Task.Run(
                () =>
                {
                    Directory.CreateDirectory(plugins.PluginsFolder);
                    return plugins.Scan(entry => ContextFor(entry.Id, entry.Settings), _lifetime.Token);
                },
                _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            log.LogError($"Reading the plugins folder failed ({reason}).", ex);
            Tell("Plugins", "Hail could not read its plugins folder. The log has the reason.");
            return;
        }

        if (_disposed)
        {
            return;
        }

        // The box lets go of the old set's rows before the old set is unloaded.
        var previous = plugins.Adopt(scan);
        _pluginRegistrations = scan.Registrations;
        _supervisor!.Replace(Compose(_pluginRegistrations));
        _model!.ProvidersReplaced();
        _box!.Refresh();

        log.LogInfo($"Plugins read ({reason}): {scan.Entries.Count} found, {scan.Registrations.Count} enabled.");
        Announce(scan.Entries);
        PluginsChanged?.Invoke();

        if (previous.Any(e => e.Provider is not null))
        {
            _ = UnloadAsync(previous);
        }
    }

    private async Task UnloadAsync(IReadOnlyList<PluginEntry> previous)
    {
        try
        {
            var report = await Task.Run(() => _plugins!.UnloadAsync(previous, PluginDisposeBudget, releaseCaches: () => WpfAssemblyCaches.ForgetCollectible())).ConfigureAwait(true);
            if (report.Lingering.Count > 0 && !_disposed)
            {
                Tell(
                    "A plugin is still in memory",
                    $"{string.Join(", ", report.Lingering)} could not be unloaded, so its old copy runs on beside the new one until Hail quits.");
            }
        }
#pragma warning disable CA1031 // Unloading is tidying; a failure is logged and Hail carries on with the new set.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.LogError("Unloading the previous plugins failed.", ex);
        }
    }

    /// <summary>Stops every plugin's threads as Hail quits. Nothing is verified: the process is ending.</summary>
    private void ShutDownPlugins()
    {
        if (_plugins is null)
        {
            return;
        }

        var loaded = _plugins.Entries.Where(e => e.Provider is { IsLoaded: true }).ToArray();
        if (loaded.Length == 0)
        {
            return;
        }

        try
        {
            var stopping = Task.Run(() => _plugins.UnloadAsync(loaded, PluginDisposeBudget, verify: false));
            if (!stopping.Wait(PluginDisposeBudget + TimeSpan.FromSeconds(1)))
            {
                log.LogError("Plugins did not stop in time; Hail quits regardless.");
            }
        }
#pragma warning disable CA1031 // Quitting; a plugin that fails to stop must not stop Hail from quitting.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.LogError("Stopping plugins failed while quitting.", ex);
        }
    }

    /// <summary>
    /// Says once a session, in a notification, that a plugin waits for an answer; clicking it
    /// asks. A plugin that waits is never loaded meanwhile.
    /// </summary>
    private void Announce(IReadOnlyList<PluginEntry> entries)
    {
        var waiting = entries
            .Where(e => e.AwaitsAnswer && _announced.Add($"{e.Id}:{e.Plugin.Files!.Fingerprint}"))
            .ToArray();

        // Nothing to invite while an answer could not be written (the log already says why).
        if (waiting.Length == 0 || !_plugins!.CanRecordAnswers)
        {
            return;
        }

        var (title, text) = waiting.Length == 1
            ? waiting[0].Status == PluginStatus.Changed
                ? ("A plugin has changed", $"{waiting[0].Name}'s files have changed since you enabled it, so it is off until you look. Click here to decide.")
                : ("A plugin is waiting", $"{waiting[0].Name} is in Hail's plugins folder and stays off until you enable it. Click here to decide.")
            : ("Plugins are waiting", $"{waiting.Length} plugins in Hail's plugins folder stay off until you decide. Click here, or find them under Plugins in the tray.");
        Notify(title, text, NoticeAction.AskAboutPlugins, warning: false);
    }

    /// <summary>A notification that is not an invitation: clicking it opens nothing.</summary>
    private void Tell(string title, string text) => Notify(title, text, NoticeAction.None);

    /// <summary>A notification whose click does <paramref name="action"/>; only the last one shown counts.</summary>
    private void Notify(string title, string text, NoticeAction action, bool warning = true)
    {
        _noticeAction = action;
        _tray?.Notify(title, text, warning);
    }

    private void OnNotificationClicked()
    {
        switch (_noticeAction)
        {
            case NoticeAction.AskAboutPlugins when _plugins?.Entries.FirstOrDefault(e => e.AwaitsAnswer) is { } next:
                AskAbout(next);
                break;
            case NoticeAction.OpenGeneral:
                OpenSettings(SettingsSection.General);
                break;
            case NoticeAction.OpenUpdates:
                OpenSettings(SettingsSection.Updates);
                break;
        }
    }

    private void AskAbout(PluginEntry entry)
    {
        if (_consent is not null)
        {
            _consent.Activate();
            return;
        }

        var window = new PluginConsentWindow(entry);
        _consent = window;
        window.Closed += (_, _) =>
        {
            _consent = null;
            if (window.Answer is { } enabled)
            {
                Answer(entry, enabled);
            }
        };

        Present(window);
    }

    private void Answer(PluginEntry entry, bool enabled)
    {
        try
        {
            _plugins!.Answer(entry, enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.LogError($"Recording the answer for plugin {entry.Id} failed.", ex);
            Tell("Plugins", $"Hail could not record your answer for {entry.Name}. The log has the reason.");
            return;
        }

        _ = ReloadPluginsAsync(enabled ? $"{entry.Id} enabled" : $"{entry.Id} kept off");
    }

    private void EditSettings(PluginEntry entry)
    {
        if (_settingsWindows.TryGetValue(entry.Id, out var open))
        {
            open.Activate();
            return;
        }

        var window = new PluginSettingsWindow(entry, _protector, values =>
        {
            try
            {
                _plugins!.SaveSettings(entry, values);

                // A scan running now read the values before this save; it runs once more.
                if (_reloading)
                {
                    _reloadAgain = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogError($"Saving the settings of plugin {entry.Id} failed.", ex);
                throw;
            }
        });
        _settingsWindows[entry.Id] = window;
        window.Closed += (_, _) => _settingsWindows.Remove(entry.Id);
        Present(window);
    }

    private static void Present(Window window)
    {
        window.Show();
        window.Activate();
        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            WindowEffects.BringToFront(source.Handle);
        }
    }

    /// <summary>The tray's Plugins menu: each plugin with where it stands and what can be done about it.</summary>
    private MenuItem PluginsMenu()
    {
        var menu = new MenuItem { Header = "Plugins" };
        var entries = _plugins?.Entries ?? [];

        foreach (var entry in entries)
        {
            menu.Items.Add(PluginItem(entry));
        }

        if (entries.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No plugins installed", IsEnabled = false });
        }

        menu.Items.Add(new Separator());

        var folder = new MenuItem { Header = "Open plugins folder" };
        folder.Click += (_, _) => _ = OpenPluginsFolderAsync();
        menu.Items.Add(folder);

        var reload = new MenuItem { Header = "Reload plugins", IsEnabled = !_reloading };
        reload.Click += (_, _) => _ = ReloadPluginsAsync("the tray");
        menu.Items.Add(reload);

        return menu;
    }

    private MenuItem PluginItem(PluginEntry entry)
    {
        var item = new MenuItem { Header = entry.AwaitsAnswer ? $"{entry.Name} (waiting)" : entry.Name };
        item.Items.Add(new MenuItem { Header = StatusLine(entry), IsEnabled = false });

        if (entry.Status is PluginStatus.New or PluginStatus.Changed or PluginStatus.Declined)
        {
            var recordable = _plugins!.CanRecordAnswers;
            var enable = new MenuItem
            {
                Header = recordable ? "Enable..." : "Enable... (Hail cannot save answers now; the log says why)",
                IsEnabled = recordable,
            };
            enable.Click += (_, _) => AskAbout(entry);
            item.Items.Add(enable);
        }

        if (entry.Status == PluginStatus.Enabled)
        {
            var off = new MenuItem { Header = "Switch off" };
            off.Click += (_, _) => Answer(entry, enabled: false);
            item.Items.Add(off);
        }

        if (entry.Status != PluginStatus.Invalid && entry.Settings.Schema.Count > 0)
        {
            var settings = new MenuItem { Header = "Settings..." };
            settings.Click += (_, _) => EditSettings(entry);
            item.Items.Add(settings);
        }

        return item;
    }

    private string StatusLine(PluginEntry entry)
    {
        var fault = _supervisor?.Faults.FirstOrDefault(f => string.Equals(f.ProviderId, entry.Id, StringComparison.Ordinal));
        return entry.Status switch
        {
            PluginStatus.Invalid => $"Cannot be used: {entry.Plugin.Problem}",
            PluginStatus.New => "Off: waiting for your answer",
            PluginStatus.Changed => "Off: its files changed since you enabled it",
            PluginStatus.Declined => "Off",
            _ when entry.Provider?.LoadProblem is { } problem => $"Could not start: {problem}",
            _ when fault is not null => $"Off until the next reload: {fault.Reason}",
            _ when entry.Provider?.IsLoaded == true => "On",
            _ => "On: loads when a search first reaches it",
        };
    }

    private async Task OpenPluginsFolderAsync()
    {
        try
        {
            Directory.CreateDirectory(paths.Plugins);
            await _launcher!.OpenPathAsync(paths.Plugins, _lifetime.Token).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Opening a folder failed; said in the log and the tray, and Hail carries on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.LogError("Opening the plugins folder failed.", ex);
            Tell("Plugins", $"The plugins folder could not be opened. It is {paths.Plugins}.");
        }
    }
}
