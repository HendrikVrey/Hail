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
}
