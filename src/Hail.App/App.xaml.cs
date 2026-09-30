using System.Diagnostics.CodeAnalysis;
using System.Windows;
using Hail.Persistence;
using Hail.Windows;
using Wpf.Ui.Appearance;

namespace Hail.App;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The Application lives as long as the process and cannot be IDisposable; the host is disposed in OnExit, and on a crash.")]
public partial class App : Application
{
    /// <summary>Exit code after an unhandled exception: EX_SOFTWARE, as Drift uses.</summary>
    private const int CrashExitCode = 70;

    private FileLog? _log;
    private HailHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var quit = e.Args.Any(a => string.Equals(a, "--quit", StringComparison.OrdinalIgnoreCase));
        var claim = SingleInstance.TryClaim();
        if (claim is null || quit)
        {
            // Another Hail is running in this session: pass the request on and leave. With
            // --quit and nothing running there is nothing to do. The library awaits with
            // ConfigureAwait(false), so blocking here cannot deadlock.
            if (claim is null)
            {
                SingleInstance.SignalAsync(quit ? InstanceCommand.Quit : InstanceCommand.Show).GetAwaiter().GetResult();
            }

            claim?.Dispose();
            Shutdown();
            return;
        }

        _log = new FileLog(HailPaths.Default);
        _log.Prune();
        HookCrashHandlers(_log);

        ApplicationThemeManager.ApplySystemTheme();

        _host = new HailHost(claim, _log, HailPaths.Default);
        _host.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }

    private void HookCrashHandlers(FileLog log)
    {
        // Hail.md §5: an unhandled exception is logged with what was being done and the
        // process exits; the next sign-in, or the Start menu shortcut, brings it back. A
        // provider's exception never reaches here (QueryRunner contains it).
        DispatcherUnhandledException += (_, args) => Crash(log, "UI thread", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Crash(log, "background thread", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            log.LogError("A background task failed and nothing was waiting for it.", args.Exception);
            args.SetObserved();
        };
    }

    private void Crash(FileLog log, string where, Exception? exception)
    {
        log.LogError($"Unhandled exception on the {where}; Hail is exiting.", exception);

        // Best effort only: the tray icon left behind would linger until the mouse passes it.
        try
        {
            _host?.Dispose();
        }
#pragma warning disable CA1031 // Already crashing; a second failure must not hide the first.
        catch (Exception cleanup)
#pragma warning restore CA1031
        {
            log.LogError("Cleaning up after the crash failed too.", cleanup);
        }

        Environment.Exit(CrashExitCode);
    }
}
