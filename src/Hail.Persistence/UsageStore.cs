using System.Text.Json;
using Hail.Core.History;

namespace Hail.Persistence;

/// <summary>
/// History on disk (<c>usage.json</c>, Hail.md §6.6), written whole and atomically. Nothing in
/// it is load-bearing: a file that cannot be read is set aside as <c>usage.unreadable.json</c>
/// (one kept, so nothing is lost without a trace) and history starts again.
/// </summary>
public sealed class UsageStore(HailPaths paths)
{
    /// <summary>Several times what <see cref="UsageHistory"/>'s own bounds allow; past it the file is not trusted.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 16,
    };

    private readonly Lock _gate = new();
    private bool _leaveFileAlone;

    /// <summary>History as it was saved, bounded and cleaned; empty when there is none.</summary>
    /// <param name="problem">A sentence for the log when the file could not be used.</param>
    public UsageHistory Load(DateTimeOffset now, out string? problem)
    {
        problem = null;
        var path = paths.Usage;
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return new UsageHistory();
            }

            try
            {
                if (new FileInfo(path).Length > MaxBytes)
                {
                    throw new InvalidDataException("the file is larger than history can be");
                }

                var snapshot = JsonSerializer.Deserialize<UsageSnapshot>(File.ReadAllText(path), Options);
                if (snapshot is not null && snapshot.Version > UsageSnapshot.CurrentVersion)
                {
                    // Written by a newer Hail: read nothing, and overwrite nothing either.
                    problem = "usage.json was written by a newer Hail; history is neither read nor saved by this one.";
                    _leaveFileAlone = true;
                    return new UsageHistory();
                }

                return UsageHistory.FromSnapshot(snapshot, now);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
            {
                problem = $"usage.json could not be read ({ex.GetType().Name}); it was set aside and history starts again.";
                SetAside(path);
                return new UsageHistory();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked by something for a moment, perhaps: not overwritten this session, so a
                // file that is fine is not replaced by an empty history.
                problem = $"usage.json could not be read ({ex.GetType().Name}); history is not used until Hail restarts.";
                _leaveFileAlone = true;
                return new UsageHistory();
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="snapshot"/> over the file, unless the file could not be read or a
    /// newer Hail wrote it, in which case it is left as it is.
    /// Throws when the disk refuses.
    /// </summary>
    public void Save(UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var text = JsonSerializer.Serialize(snapshot, Options);
        lock (_gate)
        {
            if (!_leaveFileAlone)
            {
                AtomicFile.WriteAllText(paths.Usage, text);
            }
        }
    }

    private void SetAside(string path)
    {
        try
        {
            File.Move(path, Path.Combine(paths.Root, "usage.unreadable.json"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left where it is; the next save replaces it.
        }
    }
}
