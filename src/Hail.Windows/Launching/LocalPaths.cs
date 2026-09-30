namespace Hail.Windows.Launching;

/// <summary>
/// The one rule for which paths Hail will open, show, copy or list (Hail.md §9): a full path on
/// a drive letter. Network paths (<c>\\server\share</c>), device paths (<c>\\?\</c>,
/// <c>\\.\</c>), relative paths and alternate data streams are refused, so nothing a provider
/// hands the launcher can reach a stranger's server or something that is not a file.
/// </summary>
public static class LocalPaths
{
    /// <summary>The longest path Windows accepts anywhere.</summary>
    public const int MaxLength = 32767;

    public static bool IsAcceptable(string? path) =>
        path is { Length: >= 3 and <= MaxLength }
        && char.IsAsciiLetter(path[0])
        && path[1] == ':'
        && path[2] == '\\'
        && path.IndexOf(':', 2) < 0
        && path.IndexOfAny(Path.GetInvalidPathChars()) < 0
        && !path.Contains('\0', StringComparison.Ordinal)
        && Path.IsPathFullyQualified(path);

    /// <summary>Whether <paramref name="path"/> is acceptable and something is there now.</summary>
    public static bool Exists(string? path) =>
        IsAcceptable(path) && (File.Exists(path) || Directory.Exists(path));
}
