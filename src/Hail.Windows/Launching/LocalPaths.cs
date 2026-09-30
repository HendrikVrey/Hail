namespace Hail.Windows.Launching;

/// <summary>
/// The one rule for which paths Hail will open, show, copy, list or draw an icon for (Hail.md
/// §9): a full path on a drive letter that is not a network drive. Network paths
/// (<c>\\server\share</c>), mapped network drives, device paths (<c>\\?\</c>, <c>\\.\</c>),
/// relative paths and alternate data streams are refused, so nothing a provider hands the host
/// can reach a server (and hand it the user's credentials) or something that is not a file.
/// </summary>
/// <remarks>
/// Asking a drive its type does not touch the server behind it, so the check is cheap even
/// for a mapped drive whose server is unreachable; asking whether a file exists there is not,
/// which is why the type is checked first. A folder on a local drive that is a link to a share
/// is not caught here; nothing Hail lists creates one.
/// </remarks>
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
        && Path.IsPathFullyQualified(path)
        && !IsNetworkDrive(path[0]);

    /// <summary>Whether <paramref name="path"/> is acceptable and something is there now.</summary>
    public static bool Exists(string? path) =>
        IsAcceptable(path) && (File.Exists(path) || Directory.Exists(path));

    private static bool IsNetworkDrive(char letter)
    {
        try
        {
            return new DriveInfo(letter.ToString()).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return true; // Not a drive Windows recognises: refused like one it cannot vouch for.
        }
    }
}
