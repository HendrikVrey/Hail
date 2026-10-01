namespace Hail.Core.Updates;

/// <summary>When to ask GitHub, and whether an answer is worth showing. Sling's rules.</summary>
public static class UpdateSchedule
{
    /// <summary>How often an automatic check may run.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>How long after a failed automatic check the next may run.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    /// <summary>Whether an automatic check is due.</summary>
    /// <remarks>
    /// A last check in the future means the clock was moved back; the check is due rather than
    /// postponed until the clock catches up, which could be months.
    /// </remarks>
    public static bool IsDue(DateTimeOffset? lastChecked, DateTimeOffset now) =>
        lastChecked is not { } last || last > now || now - last >= Interval;

    /// <summary>Whether <paramref name="latest"/> should be offered to someone running <paramref name="current"/>.</summary>
    /// <param name="skipped">The version the user said to skip, or null.</param>
    /// <param name="userAsked">
    /// True when the user pressed "Check now": a skipped version is offered again then, because
    /// asking is how somebody changes their mind.
    /// </param>
    public static bool ShouldOffer(ReleaseVersion current, ReleaseVersion latest, string? skipped, bool userAsked)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(latest);
        return latest > current
            && (userAsked || !string.Equals(latest.ToString(), skipped, StringComparison.Ordinal));
    }
}

/// <summary>
/// What the update check remembers between runs, in <c>update.json</c>: a file of its own, so a
/// daily write never rewrites the settings file a person may be editing.
/// </summary>
/// <param name="LastCheckedUtc">When GitHub was last asked, or null if it never has been.</param>
/// <param name="SkippedVersion">The version the user said to skip, or null.</param>
/// <param name="AskedUtc">When Hail asked whether it may check, or null if it never has.</param>
public sealed record UpdateState(DateTimeOffset? LastCheckedUtc, string? SkippedVersion, DateTimeOffset? AskedUtc)
{
    public static UpdateState Empty { get; } = new(null, null, null);
}
