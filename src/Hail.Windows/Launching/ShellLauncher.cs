using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Hail.Core.Ports;
using Hail.Sdk;
using Hail.Windows.Apps;
using Hail.Windows.Interop;

namespace Hail.Windows.Launching;

/// <summary>
/// Starts apps and opens addresses, files and folders through the shell, and is the only code
/// in Hail that starts a process (ArchitectureTests holds every other file to that).
/// </summary>
/// <remarks>
/// <para>
/// Every app goes through <c>shell:AppsFolder\&lt;id&gt;</c>, which is the path the Start
/// menu takes: a packaged app is activated by the shell's activation manager and a desktop
/// shortcut is followed with its own working folder and arguments, with no branch here.
/// </para>
/// <para>
/// The host's rules (Hail.md §9), each refused with <see cref="LaunchRefusedException"/>: an
/// app id must be one the catalog listed, so typed text never becomes a started program; an
/// address must be http or https, compared on the parsed <see cref="Uri.AbsoluteUri"/> that is
/// then what is opened; a path must pass <see cref="LocalPaths"/> and exist. Nothing is ever
/// passed through <c>cmd.exe</c>.
/// </para>
/// </remarks>
public sealed class ShellLauncher(ShellAppCatalog catalog, StaWorker worker) : ILauncher
{
    /// <summary>The user said no to Windows' elevation prompt; not an error.</summary>
    private const int ErrorCancelled = 1223;

    public ValueTask LaunchAppAsync(string appId, CancellationToken ct) =>
        StartAppAsync(appId, elevated: false, ct);

    public ValueTask LaunchAppAsAdministratorAsync(string appId, CancellationToken ct) =>
        StartAppAsync(appId, elevated: true, ct);

    public async ValueTask OpenUriAsync(Uri uri, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new LaunchRefusedException("Hail only opens web addresses that start with http or https.");
        }

        await ShellExecuteAsync(uri.AbsoluteUri, verb: null, ct).ConfigureAwait(false);
    }

    public async ValueTask OpenPathAsync(string path, CancellationToken ct)
    {
        RequireLocal(path);
        await ShellExecuteAsync(path, verb: null, ct).ConfigureAwait(false);
    }

    public async ValueTask ShowInFolderAsync(string path, CancellationToken ct)
    {
        RequireLocal(path);
        AllowForeground();

        // SHOpenFolderAndSelectItems rather than "explorer /select,<path>": nothing is put on a
        // command line, so there is nothing to quote or inject.
        var hresult = await worker.RunAsync(
            () =>
            {
                var parsed = ShellFolders.SHParseDisplayName(path, 0, out var itemList, 0, out _);
                if (parsed != 0)
                {
                    return parsed;
                }

                try
                {
                    return ShellFolders.SHOpenFolderAndSelectItems(itemList, 0, 0, 0);
                }
                finally
                {
                    ShellFolders.ILFree(itemList);
                }
            },
            ct).ConfigureAwait(false);

        Marshal.ThrowExceptionForHR(hresult);
    }

    /// <summary>Throws the refusal the user sees unless <paramref name="path"/> is local and there.</summary>
    internal static void RequireLocal(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!LocalPaths.IsAcceptable(path))
        {
            throw new LaunchRefusedException("Hail only opens files and folders on this PC's own drives.");
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new LaunchRefusedException("That file or folder is no longer there.");
        }
    }

    private async ValueTask StartAppAsync(string appId, bool elevated, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(appId);

        if (!catalog.Contains(appId))
        {
            throw new LaunchRefusedException("That app is not in the Start menu's list, so Hail will not start it.");
        }

        if (elevated && ShellAppCatalog.IsPackaged(appId))
        {
            throw new LaunchRefusedException("Windows does not start this app as administrator.");
        }

        await ShellExecuteAsync(AppsFolder.PathFor(appId), elevated ? "runas" : null, ct).ConfigureAwait(false);
    }

    private static async ValueTask ShellExecuteAsync(string target, string? verb, CancellationToken ct)
    {
        AllowForeground();

        // ShellExecuteEx can block for as long as the shell likes; it is not the UI thread's
        // to wait on. Process.Start runs it on an STA thread of its own when needed.
        await Task.Run(
            () =>
            {
                var start = new ProcessStartInfo(target) { UseShellExecute = true };
                if (verb is not null)
                {
                    start.Verb = verb;
                }

                try
                {
                    using var process = Process.Start(start);
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
                {
                    // Declined at Windows' own prompt: the user's answer, not a failure.
                }
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands every process the right to take the foreground once, before the box hides, so
    /// what was just opened comes to the front instead of flashing in the taskbar behind
    /// whatever the box was over. Hail is the foreground process here, which is the condition
    /// for being allowed to pass the right on.
    /// </summary>
    private static void AllowForeground() => User32.AllowSetForegroundWindow(User32.ASFW_ANY);
}
