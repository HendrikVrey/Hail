namespace Hail.Core.Ports;

/// <summary>A file or folder on a local drive.</summary>
/// <param name="Path">Its full path.</param>
/// <param name="Name">Its name as Explorer shows it.</param>
public sealed record LocalItem(string Path, string Name, bool IsFolder);

/// <summary>What asking the Windows Search index found.</summary>
public abstract record FileIndexAnswer
{
    private FileIndexAnswer()
    {
    }

    public sealed record Found(IReadOnlyList<LocalItem> Items) : FileIndexAnswer;

    /// <summary>The index could not be asked (the Windows Search service is off, say).</summary>
    /// <param name="Reason">A sentence for the user.</param>
    public sealed record Unavailable(string Reason) : FileIndexAnswer;
}

/// <summary>Files by name from the Windows Search index (Hail.md §7.2). <c>Hail.Windows</c> implements it.</summary>
public interface IFileIndex
{
    Task<FileIndexAnswer> SearchAsync(string search, int limit, CancellationToken ct);
}

/// <summary>What listing a folder found.</summary>
/// <param name="Items">Its entries, folders and files, hidden and system ones left out.</param>
/// <param name="Exists">False when there is no such folder, or it could not be read.</param>
public sealed record FolderListing(IReadOnlyList<LocalItem> Items, bool Exists)
{
    public static FolderListing Missing { get; } = new([], Exists: false);
}

/// <summary>
/// The local disk as the files provider sees it: a folder listed for path mode, and whether a
/// remembered file still exists. <c>Hail.Windows</c> implements it.
/// </summary>
public interface ILocalFiles
{
    /// <summary>The user's own folder, which <c>~</c> stands for.</summary>
    string HomeFolder { get; }

    /// <summary>
    /// The first <paramref name="max"/> entries of <paramref name="folder"/>, which must be a
    /// full path on a local drive; anything else is <see cref="FolderListing.Missing"/>.
    /// </summary>
    FolderListing List(string folder, int max, CancellationToken ct);

    /// <summary>The item at <paramref name="path"/>, or null when it is not there now.</summary>
    LocalItem? Describe(string path);

    /// <summary>
    /// Whether the drive <paramref name="path"/> is on is there, so a missing item is known to
    /// be gone rather than on a USB stick that is unplugged.
    /// </summary>
    bool IsDrivePresent(string path);
}
