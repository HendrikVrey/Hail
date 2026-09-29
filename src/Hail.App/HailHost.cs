using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Hail.Core.Hosting;
using Hail.Core.Matching;
using Hail.Persistence;
using Hail.Providers.Apps;
using Hail.Windows;
using Hail.Windows.Apps;
using Hail.Windows.Hotkeys;
using Hail.Windows.Icons;
using Hail.Windows.Tray;
using Hail.Windows.Windowing;
using Wpf.Ui.Appearance;

namespace Hail.App;

/// <summary>
/// The composition root and the process's lifetime: it builds everything once, answers the
/// hotkey, the tray and a second start, and takes it all down on Quit.
/// </summary>
internal sealed class HailHost(SingleInstance instance, FileLog log) : IDisposable
{
    private readonly HotkeyChord _chord = HotkeyChord.AltSpace;
    private readonly CancellationTokenSource _lifetime = new();

    private StaWorker? _worker;
    private ShellAppCatalog? _catalog;
    private SearchWindow? _box;
    private HostWindow? _host;
    private TrayIcon? _tray;
    private bool _hotkeyRegistered;
    private bool _disposed;

    public void Start()
    {
        log.LogInfo($"Hail {Version()} starting.");

        _worker = new StaWorker("Hail shell worker");
        _catalog = new ShellAppCatalog(_worker);

        // Every provider is registered by hand, in order: no assembly scanning at startup.
        var matcher = new FuzzyMatcher();
        var apps = new ProviderRegistration(
            AppsProvider.ProviderId,
            new AppsProvider(_catalog),
            new PluginContext(AppsProvider.ProviderId, new ShellAppLauncher(_catalog), matcher, log));
        var runner = new QueryRunner([apps], log);

        var model = new SearchViewModel(runner, new IconCache(new ShellIcons(_worker)), log);
        _box = new SearchWindow(model, log);
        _box.Prepare();

        _host = new HostWindow();
        _host.HotkeyPressed += Toggle;
        _host.TrayActivated += OnTray;
        _host.TaskbarCreated += () => _tray?.Restore();
        _host.ThemeChanged += OnThemeChanged;

        _tray = new TrayIcon(_host.Handle, $"Hail ({_chord.Display})");
        RegisterHotkey();

        // The window is ready; now the Start menu is read, so the first summon already has it.
        _ = RefreshCatalogAsync("startup");
        _ = ListenForSecondStartAsync();
    }

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

        _tray?.Dispose();
        _box?.CloseForQuit();
        _host?.Dispose();
        _worker?.Dispose();
        instance.Dispose();
        _lifetime.Dispose();
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
        _tray!.Notify(
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
        }
    }

    private void ShowTrayMenu()
    {
        var open = new MenuItem { Header = "Open Hail", InputGestureText = _hotkeyRegistered ? _chord.Display : string.Empty };
        open.Click += (_, _) => Summon();

        var quit = new MenuItem { Header = "Quit Hail" };
        quit.Click += (_, _) => Quit("the tray");

        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
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
