using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Hail.Plugins;

/// <summary>
/// Every file in a plugin's folder and its SHA-256, and one fingerprint over all of them: what
/// the user's answer is pinned to (Hail.md §9), and what the load context checks each assembly
/// against before it loads it.
/// </summary>
/// <param name="Fingerprint">Lower-case hex; changes when any file is added, removed, renamed or changed.</param>
/// <param name="Hashes">Each file's lower-case hex SHA-256, by its path relative to the folder, lower-cased.</param>
public sealed record PluginFiles(string Fingerprint, IReadOnlyDictionary<string, string> Hashes)
{
    /// <summary>More files than a plugin with its dependencies has.</summary>
    public const int MaxFiles = 2000;

    /// <summary>More than a plugin with its dependencies weighs; hashing it at startup must stay cheap.</summary>
    public const long MaxBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Reads and hashes <paramref name="folder"/>. Throws <see cref="PluginRefusedException"/>
    /// with a sentence when the folder cannot be a plugin's: it holds a link (whose target
    /// could change without the folder changing), or more than a plugin should.
    /// </summary>
    public static PluginFiles Read(string folder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
        };

        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new PluginRefusedException($"It holds a link ({Relative(folder, entry.FullName)}); a plugin's folder must hold its own files.");
            }

            if (entry is not FileInfo file)
            {
                continue;
            }

            total += file.Length;
            if (hashes.Count >= MaxFiles || total > MaxBytes)
            {
                throw new PluginRefusedException($"Its folder holds more than a plugin can ({MaxFiles} files or {MaxBytes / (1024 * 1024)} MB).");
            }

            hashes[Relative(folder, file.FullName)] = HashOf(file.FullName);
        }

        return new PluginFiles(FingerprintOf(hashes), hashes);
    }

    /// <summary>The key a file is known by in <see cref="Hashes"/>.</summary>
    public static string Relative(string folder, string path) =>
        Path.GetRelativePath(folder, path).Replace('/', '\\').ToLowerInvariant();

    /// <summary>The lower-case hex SHA-256 of <paramref name="bytes"/>.</summary>
    public static string HashOf(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string HashOf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string FingerprintOf(SortedDictionary<string, string> hashes)
    {
        var text = new StringBuilder();
        foreach (var (path, hash) in hashes)
        {
            text.Append(CultureInfo.InvariantCulture, $"{path}\n{hash}\n");
        }

        return HashOf(Encoding.UTF8.GetBytes(text.ToString()));
    }
}

/// <summary>
/// The host will not use a plugin, for a reason it can say in a sentence. The message is the
/// host's own words, never the plugin's, so it may be shown and logged as it is.
/// </summary>
public sealed class PluginRefusedException : Exception
{
    public PluginRefusedException()
        : base("The plugin cannot be used.")
    {
    }

    public PluginRefusedException(string message)
        : base(message)
    {
    }

    public PluginRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
