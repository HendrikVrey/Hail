using System.Text;

namespace Hail.Persistence;

/// <summary>
/// Replaces a file whole or not at all, Etch's way: the new text goes to a temporary file
/// beside it, is flushed to the disk, and is then moved over the old one. A crash or a full
/// disk part way leaves the old file as it was, never half of each.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);

        var folder = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The path has no folder.", nameof(path));
        Directory.CreateDirectory(folder);

        var temporary = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The write already failed and that failure is what the caller hears about; a
            // stray temporary file is named so it is never mistaken for the real one.
        }
    }
}
