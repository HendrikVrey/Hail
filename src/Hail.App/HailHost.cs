using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Hail.Core.History;
using Hail.Core.Hosting;
using Hail.Core.Matching;
using Hail.Core.Ports;
using Hail.Core.Settings;
using Hail.Persistence;
using Hail.Plugins;
using Hail.Providers.Apps;
using Hail.Providers.Calculator;
using Hail.Providers.Commands;
using Hail.Providers.Files;
using Hail.Providers.Web;
using Hail.Sdk;
using Hail.Windows;
using Hail.Windows.Apps;
using Hail.Windows.Files;
using Hail.Windows.Hotkeys;
using Hail.Windows.Icons;
using Hail.Windows.Launching;
using Hail.Windows.Security;
using Hail.Windows.Session;
using Hail.Windows.Startup;
using Hail.Windows.Tray;
using Hail.Windows.Windowing;
using Wpf.Ui.Appearance;

namespace Hail.App;

/// <summary>
/// The composition root and the process's lifetime: it builds everything once, answers the
/// hotkey, the tray and a second start, and takes it all down on Quit. Plugins are read after
/// the box is ready and again on every Reload plugins (Hail.md §6.3; HailHost.Plugins.cs).
/// </summary>
internal sealed partial class HailHost(SingleInstance instance, FileLog log, HailPaths paths) : IDisposable, IHostCommands
{
    /// <summary>
    /// How long typing must pause before the Windows Search index is asked: a word is one
    /// query, not one per letter (Hail.md §6.4).
    /// </summary>
    private static readonly TimeSpan FilesDebounce = TimeSpan.FromMilliseconds(150);

    /// <summary>History is written this long after the last change, so a burst of picks is one write.</summary>
    private static readonly TimeSpan HistorySaveDelay = TimeSpan.FromSeconds(2);

    private readonly HotkeyChord _chord = HotkeyChord.AltSpace;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StartupRegistration _startup = new();
    private readonly Lock _saveGate = new();

    private StaWorker? _worker;
    private ShellAppCatalog? _catalog;
    private ShellLauncher? _launcher;
    private Supervisor? _supervisor;
    private UsageStore? _usageStore;
    private UsageHistory? _history;
    private HailSettings? _settings;
    private FuzzyMatcher? _matcher;
    private WpfClipboard? _clipboard;
    private IReadOnlyList<ProviderRegistration> _builtIns = [];
    private SearchViewModel? _model;
    private SearchWindow? _box;
    private HostWindow? _host;
    private TrayIcon? _tray;
    private bool _hotkeyRegistered;
    private bool _savePending;
    private bool _disposed;

    public string SettingsPath => paths.Settings;

    public string PluginsFolder => paths.Plugins;

    public void Start()
    {
        log.LogInfo($"Hail {Version()} starting.");

        var settings = LoadSettings();
        _settings = settings;
        _history = LoadHistory();
        _history.Changed += ScheduleHistorySave;

        _worker = new StaWorker("Hail shell worker");
        _catalog = new ShellAppCatalog(_worker);
        _launcher = new ShellLauncher(_catalog, _worker);
        _matcher = new FuzzyMatcher();
        _clipboard = new WpfClipboard(Application.Current.Dispatcher);
        _plugins = CreatePluginManager();

        _builtIns = BuiltIns(settings);
        _supervisor = new Supervisor(Compose([]), log);
        _supervisor.FaultsChanged += () =>
        {
            if (_supervisor.Faults.Count > 0)
            {
                log.LogInfo($"Providers switched off: {string.Join(", ", _supervisor.Faults.Select(f => f.ProviderId))}.");
            }
        };

        _model = new SearchViewModel(_supervisor, _history, new IconCache(new ShellIcons(_worker)), log);
        _box = new SearchWindow(_model, log, settings.KeepLastQuery);
        _box.SettingsRequested += () => _ = OpenSettingsAsync();
        _box.Prepare();

        _host = new HostWindow();
        _host.HotkeyPressed += Toggle;
        _host.TrayActivated += OnTray;
        _host.TaskbarCreated += () => _tray?.Restore();
        _host.ThemeChanged += OnThemeChanged;

        _tray = new TrayIcon(_host.Handle, $"Hail ({_chord.Display})");
        RegisterHotkey();

        // The window is ready; now the Start menu is read, so the first summon already has it,
        // and the plugins folder, whose plugins load only when a query first reaches them.
        _ = RefreshCatalogAsync("startup");
        _ = ReloadPluginsAsync("startup");
        _ = ListenForSecondStartAsync();
    }

    /// <summary>The box's "Quit Hail": after the action that asked has finished and the box has hidden.</summary>
    public void Quit() =>
        Application.Current.Dispatcher.BeginInvoke(() => Quit("the box"), System.Windows.Threading.DispatcherPriority.Background);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();

        if (_hotkeyRegistered && _host is not null)
        {
            GlobalHotkey.Unregister(_host.Handle, HostWindow.HotkeyId);
        }

        SaveHistoryNow("quitting", onlyIfChanged: true);
        ShutDownPlugins();

        _tray?.Dispose();
        _box?.CloseForQuit();
        _host?.Dispose();
        _worker?.Dispose();
        instance.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>
    /// Every built-in provider, registered by hand and in order (no assembly scanning at
    /// startup). The order breaks ties: apps before files of the same name, and the web search
    /// last; plugins go in before it (<see cref="Compose"/>).
    /// </summary>
    private List<ProviderRegistration> BuiltIns(HailSettings settings)
    {
        PluginContext Context(string id) => ContextFor(id, settings: null);

        var web = new WebSearchProvider(settings.WebSearch);
        foreach (var problem in web.Load())
        {
            log.LogError(problem);
        }

        var all = new List<ProviderRegistration>
        {
            new(AppsProvider.ProviderId, "Apps", new AppsProvider(_catalog!), Context(AppsProvider.ProviderId)),
            new(CalculatorProvider.ProviderId, "Calculator", new CalculatorProvider(CultureInfo.CurrentCulture), Context(CalculatorProvider.ProviderId))
            {
                Keywords = [new ProviderKeyword(CalculatorProvider.Keyword, "Calculator")],
            },
            new(FilesProvider.ProviderId, "Files", new FilesProvider(new WindowsSearchIndex(), new LocalFiles()), Context(FilesProvider.ProviderId))
            {
                Debounce = FilesDebounce,
            },
            new(CommandsProvider.ProviderId, "Commands", new CommandsProvider(new SessionControl(), this), Context(CommandsProvider.ProviderId)),
            new(WebSearchProvider.ProviderId, "Web search", web, Context(WebSearchProvider.ProviderId))
            {
                Keywords = [.. web.Keywords.Select(k => new ProviderKeyword(k.Keyword, k.Name))],
            },
        };

        return all;
    }

    /// <summary>
    /// The providers in force: the built-ins, then the enabled plugins, then the web search, so
    /// a plugin outranks the web search in a tie and never a built-in; anything switched off
    /// in settings left out.
    /// </summary>
    private List<ProviderRegistration> Compose(IReadOnlyList<ProviderRegistration> plugins)
    {
        var ordered = _builtIns.Where(p => p.Id != WebSearchProvider.ProviderId)
            .Concat(plugins)
            .Concat(_builtIns.Where(p => p.Id == WebSearchProvider.ProviderId))
            .ToList();

        foreach (var off in ordered.Where(p => !_settings!.IsEnabled(p.Id)))
        {
            log.LogInfo($"Provider {off.Id} is switched off in settings.");
        }

        var enabled = ordered.Where(p => _settings!.IsEnabled(p.Id)).ToList();
        ReportKeywordClashes(enabled);
        return enabled;
    }

    /// <summary>What a provider is given: the host's services, its settings, its part of history and a data folder.</summary>
    private PluginContext ContextFor(string id, IPluginSettings? settings) =>
        new(
            id,
            _launcher!,
            _matcher!,
            _clipboard!,
            log,
            settings,
            new ProviderHistory(_history!, id),
            () =>
            {
                var folder = paths.DataFolderFor(id);
                Directory.CreateDirectory(folder);
                return folder;
            });

    private void ReportKeywordClashes(IReadOnlyList<ProviderRegistration> providers)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            foreach (var keyword in provider.Keywords)
            {
                if (!owners.TryAdd(keyword.Keyword, provider.Id))
                {
                    log.LogError($"The keyword \"{keyword.Keyword}\" of {provider.Id} is already {owners[keyword.Keyword]}'s; the first keeps it.");
                }
            }
        }
    }

    private HailSettings LoadSettings()
    {
        var load = new SettingsStore(paths).Load();
        foreach (var problem in load.Problems)
        {
            log.LogError($"Settings: {problem}");
        }

        return load.Settings;
    }

    private UsageHistory LoadHistory()
    {
        _usageStore = new UsageStore(paths);
        var history = _usageStore.Load(DateTimeOffset.UtcNow, out var problem);
        if (problem is not null)
        {
            log.LogError($"History: {problem}");
        }

        return history;
    }

    private void ScheduleHistorySave()
    {
        lock (_saveGate)
        {
            if (_savePending || _disposed)
            {
                return;
            }

            _savePending = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(HistorySaveDelay, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // Quitting; Dispose saves.
            }

            SaveHistoryNow("a pick");
        });
    }

    /// <param name="onlyIfChanged">True on quitting: a history nobody changed is not rewritten.</param>
    private void SaveHistoryNow(string reason, bool onlyIfChanged = false)
    {
        lock (_saveGate)
        {
            if (onlyIfChanged && !_savePending)
            {
                return;
            }

            _savePending = false;
        }

        if (_history is null || _usageStore is null)
        {
            return;
        }

        try
        {
            _usageStore.Save(_history.ToSnapshot());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError($"Saving history failed ({reason}); it is tried again on the next change.", ex);
        }
    }

    private void RegisterHotkey()
    {
        var error = GlobalHotkey.Register(_host!.Handle, HostWindow.HotkeyId, _chord);
        _hotkeyRegistered = error == 0;
        if (_hotkeyRegistered)
        {
            log.LogInfo($"{_chord.Display} registered.");
            return;
        }

        // Hail.md §8: Hail still runs, and says so, rather than running without a way in.
        log.LogError($"{_chord.Display} could not be registered (Win32 error {error}).");
        Tell(
            $"{_chord.Display} is taken",
            error == GlobalHotkey.ErrorAlreadyRegistered
                ? $"Another program already uses {_chord.Display} (PowerToys Run often does). Click the Hail icon to open the box."
                : $"Windows would not give Hail {_chord.Display}. Click the Hail icon to open the box.");
    }

    private void Toggle()
    {
        if (_box!.IsSummoned)
        {
            _box.Dismiss();
            return;
        }

        Summon();
    }

    private void Summon()
    {
        _box!.Summon();

        if (_catalog!.IsStale)
        {
            _ = RefreshCatalogAsync("summon");
        }
    }

    private void OnTray(TrayEvent trayEvent)
    {
        switch (trayEvent)
        {
            case TrayEvent.Select:
                Summon();
                break;
            case TrayEvent.ContextMenu:
                ShowTrayMenu();
                break;
            case TrayEvent.NotificationClicked:
                AskAboutNextPlugin();
                break;
        }
    }

    private void ShowTrayMenu()
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        var open = new MenuItem { Header = "Open Hail", InputGestureText = _hotkeyRegistered ? _chord.Display : string.Empty };
        open.Click += (_, _) => Summon();
        menu.Items.Add(open);
        menu.Items.Add(new Separator());

        menu.Items.Add(StartupItem());
        menu.Items.Add(PluginsMenu());

        var settings = new MenuItem { Header = "Open settings file" };
        settings.Click += (_, _) => _ = OpenSettingsAsync();
        menu.Items.Add(settings);

        var clear = new MenuItem { Header = "Clear history", IsEnabled = _history?.Count > 0 };
        clear.Click += (_, _) =>
        {
            _history?.Clear();
            log.LogInfo("History cleared from the tray.");
        };
        menu.Items.Add(clear);

        // A provider switched off says so here (Hail.md §6.5), until the settings window exists.
        var faults = _supervisor?.Faults ?? [];
        if (faults.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var fault in faults)
            {
                menu.Items.Add(new MenuItem { Header = $"{fault.ProviderName} is off: {fault.Reason}", IsEnabled = false });
            }
        }

        menu.Items.Add(new Separator());
        var quit = new MenuItem { Header = "Quit Hail" };
        quit.Click += (_, _) => Quit("the tray");
        menu.Items.Add(quit);

        menu.IsOpen = true;

        // A menu opened from the notification area belongs to a process that is not in the
        // foreground, so a click elsewhere would never close it. Taking the foreground for the
        // menu's own window is what makes it behave like every other tray menu.
        if (PresentationSource.FromVisual(menu) is HwndSource source)
        {
            WindowEffects.BringToFront(source.Handle);
        }
    }

    /// <summary>"Start with Windows", read from the registry each time the menu opens (Hail.md §5).</summary>
    private MenuItem StartupItem()
    {
        var executable = Environment.ProcessPath;
        var state = executable is null ? StartupState.Off : _startup.StateFor(executable);
        var item = new MenuItem
        {
            Header = state switch
            {
                StartupState.DisabledByUser => "Start with Windows (switched off in Task Manager)",
                StartupState.OnElsewhere => "Start with Windows (another copy of Hail is registered)",
                _ => "Start with Windows",
            },
            IsCheckable = true,
            IsChecked = state == StartupState.On,
            IsEnabled = executable is not null,
        };

        item.Click += (_, _) =>
        {
            try
            {
                if (state == StartupState.On)
                {
                    _startup.Disable();
                    log.LogInfo("Start at sign-in switched off.");
                }
                else
                {
                    _startup.Enable(executable!);
                    log.LogInfo("Start at sign-in switched on.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                log.LogError("Changing start at sign-in failed.", ex);
                Tell("Start with Windows", "Windows would not let Hail change this. The log has the reason.");
            }
        };

        return item;
    }

    private async Task OpenSettingsAsync()
    {
        try
        {
            await _launcher!.OpenPathAsync(paths.Settings, _lifetime.Token).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Opening an editor failed; said in the log and the tray, and Hail carries on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.LogError("Opening the settings file failed.", ex);
            Tell("Hail settings", $"The settings file could not be opened. It is {paths.Settings}.");
        }
    }

    private void OnThemeChanged()
    {
        ApplicationThemeManager.ApplySystemTheme();
        _box?.ApplyTheme();
    }

    private void Quit(string from)
    {
        if (_disposed)
        {
            return;
        }

        log.LogInfo($"Quit from {from}.");
        Dispose();
        Application.Current.Shutdown();
    }

    private async Task RefreshCatalogAsync(string reason)
    {
        try
        {
            var refresh = await _catalog!.RefreshAsync(_lifetime.Token).ConfigureAwait(true);
            if (refresh is not null)
            {
                log.LogInfo($"Start menu read ({reason}): {refresh.Count} apps, {refresh.Packaged} packaged, in {refresh.Elapsed.TotalMilliseconds:0} ms.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Quitting.
        }
#pragma warning disable CA1031 // Reading the Start menu failed; the previous list stays and the next summon tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.LogError($"Reading the Start menu failed ({reason}).", ex);
        }
    }

    private async Task ListenForSecondStartAsync()
    {
        try
        {
            await instance.ListenAsync(
                command => Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (command == InstanceCommand.Quit)
                    {
                        Quit("a second start with --quit");
                    }
                    else if (!_disposed)
                    {
                        Summon();
                    }
                }),
                _lifetime.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Without the pipe a second start cannot summon the box; Hail itself carries on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.LogError("Listening for a second start stopped.", ex);
        }
    }

    private static string Version() =>
        typeof(HailHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
}
