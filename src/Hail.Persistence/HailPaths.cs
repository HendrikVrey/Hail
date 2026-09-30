namespace Hail.Persistence;

/// <summary>Where Hail keeps its files. One root, so a test can point all of it elsewhere.</summary>
public sealed class HailPaths(string root)
{
    public static HailPaths Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hail"));

    public string Root { get; } = root;

    public string Logs => Path.Combine(Root, "logs");

    public string Settings => Path.Combine(Root, "settings.json");

    /// <summary>History: which results were picked, for what (Hail.md §6.6).</summary>
    public string Usage => Path.Combine(Root, "usage.json");

    /// <summary>Where plugins are installed, one folder each, named after the plugin's id (Hail.md §6.1).</summary>
    public string Plugins => Path.Combine(Root, "plugins");

    /// <summary>What the user answered for each plugin, pinned to its files' fingerprint (Hail.md §9).</summary>
    public string PluginApprovals => Path.Combine(Root, "plugins.json");

    /// <summary>The values of plugins' settings; secrets in it are encrypted for the account.</summary>
    public string PluginSettings => Path.Combine(Root, "plugin-settings.json");

    /// <summary>Each provider's own data folder, outside the plugin's installed files.</summary>
    public string DataFolderFor(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (providerId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || providerId.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("A provider id is not a folder name.", nameof(providerId));
        }

        return Path.Combine(Root, "data", providerId);
    }
}
