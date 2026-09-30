using System.ComponentModel;
using System.Runtime.InteropServices;
using Hail.Core.Ports;
using Hail.Windows.Interop;

namespace Hail.Windows.Session;

/// <summary>
/// Lock, sleep, sign out, restart and shut down, through Windows' own calls (Hail.md §7.5).
/// Each throws <see cref="Win32Exception"/> when Windows refuses. Whether to ask first is the
/// commands provider's business; by the time a method here runs, the user has said yes.
/// </summary>
public sealed class SessionControl : ISessionControl
{
    public void Lock() => Check(Session32.LockWorkStation());

    public void Sleep()
    {
        EnableShutdownPrivilege();
        Check(Session32.SetSuspendState(hibernate: false, force: false, wakeupEventsDisabled: false));
    }

    public void SignOut() => Check(Session32.ExitWindowsEx(Session32.EWX_LOGOFF, Session32.ReasonPlannedOther));

    public void Restart()
    {
        EnableShutdownPrivilege();
        Check(Session32.ExitWindowsEx(Session32.EWX_REBOOT, Session32.ReasonPlannedOther));
    }

    public void ShutDown()
    {
        EnableShutdownPrivilege();
        Check(Session32.ExitWindowsEx(Session32.EWX_POWEROFF, Session32.ReasonPlannedOther));
    }

    private static void Check(bool succeeded)
    {
        if (!succeeded)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>
    /// Every user holds the shutdown privilege, but a process starts with it switched off;
    /// restarting, shutting down and sleeping all need it switched on in Hail's own token.
    /// </summary>
    private static void EnableShutdownPrivilege()
    {
        Check(Advapi32.OpenProcessToken(
            Kernel32.GetCurrentProcess(),
            Advapi32.TOKEN_ADJUST_PRIVILEGES | Advapi32.TOKEN_QUERY,
            out var token));
        try
        {
            Check(Advapi32.LookupPrivilegeValue(null, Advapi32.SE_SHUTDOWN_NAME, out var luid));
            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = Advapi32.SE_PRIVILEGE_ENABLED };
            Check(Advapi32.AdjustTokenPrivileges(token, disableAll: false, ref privileges, 0, 0, 0));

            // AdjustTokenPrivileges succeeds even when it assigned nothing; this is how it says so.
            var error = Marshal.GetLastPInvokeError();
            if (error == Advapi32.ERROR_NOT_ALL_ASSIGNED)
            {
                throw new Win32Exception(error);
            }
        }
        finally
        {
            Kernel32.CloseHandle(token);
        }
    }
}
