using Microsoft.Win32;

namespace Hail.Windows.Startup;

/// <summary>Whether Hail starts when the user signs in.</summary>
public enum StartupState
{
    /// <summary>Nothing registered.</summary>
    Off,

    /// <summary>Registered for this copy of Hail, and Windows will start it.</summary>
    On,

    /// <summary>Registered, but for a copy of Hail somewhere else (an older build, another folder).</summary>
    OnElsewhere,

    /// <summary>Registered, and switched off in Task Manager's Startup apps.</summary>
    DisabledByUser,
}

/// <summary>
/// Starting Hail at sign-in (Hail.md §5): a value under the user's own <c>Run</c> key, the
/// place every per-user app uses. No elevation and no scheduled task; the registry itself is
/// the only record, so there is no setting to fall out of step with it.
/// </summary>
/// <remarks>
/// Task Manager switches a Run entry off by writing a flag under <c>StartupApproved\Run</c>
/// rather than deleting the entry. Hail reads that flag, and turning start-at-sign-in on from
/// Hail clears it, since that is the user asking again.
/// </remarks>
public sealed class StartupRegistration(
    string runKey = StartupRegistration.RunKey,
    string approvedKey = StartupRegistration.ApprovedKey,
    string valueName = StartupRegistration.DefaultValueName)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string DefaultValueName = "Hail";

    /// <summary>The first byte of a StartupApproved value: 2 (or any even value) on, 3 off.</summary>
    private const byte DisabledFlag = 0x03;

    public StartupState StateFor(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        using var run = Registry.CurrentUser.OpenSubKey(runKey);
        if (run?.GetValue(valueName) is not string command || string.IsNullOrWhiteSpace(command))
        {
            return StartupState.Off;
        }

        if (IsDisabledInTaskManager())
        {
            return StartupState.DisabledByUser;
        }

        return string.Equals(command.Trim(), CommandFor(executable), StringComparison.OrdinalIgnoreCase)
            ? StartupState.On
            : StartupState.OnElsewhere;
    }

    /// <summary>Registers <paramref name="executable"/> to start at sign-in, replacing any other copy.</summary>
    public void Enable(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("The executable must be a full path.", nameof(executable));
        }

        using (var run = Registry.CurrentUser.CreateSubKey(runKey, writable: true))
        {
            run.SetValue(valueName, CommandFor(executable), RegistryValueKind.String);
        }

        using var approved = Registry.CurrentUser.OpenSubKey(approvedKey, writable: true);
        approved?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public void Disable()
    {
        using var run = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
        run?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <summary>The command line Windows runs: the path quoted, since it may hold spaces.</summary>
    public static string CommandFor(string executable) => $"\"{executable}\"";

    private bool IsDisabledInTaskManager()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(approvedKey);
        return approved?.GetValue(valueName) is byte[] { Length: > 0 } flags && flags[0] == DisabledFlag;
    }
}
