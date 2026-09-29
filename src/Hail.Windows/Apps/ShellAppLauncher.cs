using System.Diagnostics;
using Hail.Core.Ports;
using Hail.Sdk;
using Hail.Windows.Interop;

namespace Hail.Windows.Apps;

/// <summary>An app launch the host would not make.</summary>
public sealed class LaunchRefusedException(string message) : Exception(message);

/// <summary>
/// Starts apps through the shell, and is the only code in Hail that starts a process
/// (ArchitectureTests holds every other file to that).
/// </summary>
/// <remarks>
/// <para>
/// Every app goes through <c>shell:AppsFolder\&lt;id&gt;</c>, which is the path the Start
/// menu takes: a packaged app is activated by the shell's activation manager and a desktop
/// shortcut is followed with its own working folder and arguments, with no branch here.
/// </para>
/// <para>
/// The id must be one the catalog listed (Hail.md §9: started from an index, never from typed
/// text). A provider, or a plugin, cannot hand this a path of its own making.
/// </para>
/// </remarks>
public sealed class ShellAppLauncher(ShellAppCatalog catalog) : ILauncher
{
    public async ValueTask LaunchAppAsync(string appId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(appId);

        if (!catalog.Contains(appId))
        {
            throw new LaunchRefusedException("That app is not in the Start menu's list, so Hail will not start it.");
        }

        // Handing every process the right to take the foreground once, before the box hides,
        // so the app just started comes to the front instead of flashing in the taskbar
        // behind whatever the box was over. Hail is the foreground process here, which is
        // the condition for being allowed to pass the right on.
        User32.AllowSetForegroundWindow(User32.ASFW_ANY);

        // ShellExecuteEx can block for as long as the shell likes; it is not the UI thread's
        // to wait on. Process.Start runs it on an STA thread of its own when needed.
        await Task.Run(
            () =>
            {
                var start = new ProcessStartInfo(AppsFolder.PathFor(appId)) { UseShellExecute = true };
                using var process = Process.Start(start);
            },
            ct).ConfigureAwait(false);
    }
}
