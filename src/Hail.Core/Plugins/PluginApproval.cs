namespace Hail.Core.Plugins;

/// <summary>Where a plugin found in the plugins folder stands (Hail.md §9).</summary>
public enum PluginStatus
{
    /// <summary>Its folder or manifest cannot be used; the reason says why. Nothing of it is loaded.</summary>
    Invalid,

    /// <summary>Never answered: off until the user enables it.</summary>
    New,

    /// <summary>Enabled, and its files are the ones that were enabled: it loads on first use.</summary>
    Enabled,

    /// <summary>The user kept it off. Not asked again until its files change.</summary>
    Declined,

    /// <summary>Enabled once, but its files have changed since: off until the user enables it again.</summary>
    Changed,
}

/// <summary>
/// What the user answered for a plugin, pinned to the fingerprint of the files they were
/// answering about (Hail.md §9): a folder that changes afterwards is asked about again.
/// </summary>
public sealed record PluginApproval(bool Enabled, string Fingerprint)
{
    public static PluginStatus StatusOf(PluginApproval? approval, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (approval is null)
        {
            return PluginStatus.New;
        }

        var same = string.Equals(approval.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase);
        return (approval.Enabled, same) switch
        {
            (true, true) => PluginStatus.Enabled,
            (true, false) => PluginStatus.Changed,
            (false, true) => PluginStatus.Declined,

            // Kept off, then changed: a new version is a new question.
            (false, false) => PluginStatus.New,
        };
    }
}
