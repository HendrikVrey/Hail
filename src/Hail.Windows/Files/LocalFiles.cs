using System.Security;
using Hail.Core.Ports;
using Hail.Windows.Launching;

namespace Hail.Windows.Files;

/// <summary>
/// The local disk for the files provider: a folder's entries for path mode, and whether a
/// remembered file is still there. Only paths <see cref="LocalPaths"/> accepts are touched.
/// </summary>
public sealed class LocalFiles : ILocalFiles
{
    private static readonly EnumerationOptions Listing = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    public string HomeFolder { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public FolderListing List(string folder, int max, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentOutOfRangeException.ThrowIfNegative(max);

        if (!LocalPaths.IsAcceptable(folder))
        {
            return FolderListing.Missing;
        }

        try
        {
            var directory = new DirectoryInfo(folder);
            if (!directory.Exists)
            {
                return FolderListing.Missing;
            }

            var items = new List<LocalItem>();
            foreach (var entry in directory.EnumerateFileSystemInfos("*", Listing))
            {
                ct.ThrowIfCancellationRequested();
                if (items.Count >= max)
                {
                    break;
                }

                items.Add(new LocalItem(entry.FullName, entry.Name, entry is DirectoryInfo));
            }

            return new FolderListing(items, Exists: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return FolderListing.Missing;
        }
    }

    public bool IsDrivePresent(string path) =>
        LocalPaths.IsAcceptable(path) && Directory.Exists(Path.GetPathRoot(path));

    public LocalItem? Describe(string path)
    {
        if (!LocalPaths.IsAcceptable(path))
        {
            return null;
        }

        if (File.Exists(path))
        {
            return new LocalItem(path, Path.GetFileName(path), IsFolder: false);
        }

        if (Directory.Exists(path))
        {
            var trimmed = Path.TrimEndingDirectorySeparator(path);
            var name = Path.GetFileName(trimmed);
            return new LocalItem(path, name.Length > 0 ? name : trimmed, IsFolder: true);
        }

        return null;
    }
}
