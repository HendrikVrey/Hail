using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hail.Core.Plugins;

namespace Hail.Persistence;

/// <summary>
/// What the user answered for each plugin (<c>plugins.json</c>, Hail.md §9): enabled or kept
/// off, pinned to the fingerprint of the files that were answered about. Thread-safe.
/// </summary>
/// <remarks>
/// A file that cannot be read is set aside as <c>plugins.unreadable.json</c> and every plugin
/// counts as never answered, so the worst a damaged file does is ask again; it never enables
/// anything. A file a newer Hail wrote is neither read nor overwritten.
/// </remarks>
public sealed partial class PluginApprovalStore(HailPaths paths)
{
    public const int CurrentVersion = 1;
    public const int MaxBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly Lock _gate = new();
    private Dictionary<string, PluginApproval> _approvals = new(StringComparer.OrdinalIgnoreCase);
    private bool _leaveFileAlone;

    /// <summary>Reads the file; a sentence for the log when it could not be used.</summary>
    public string? Load()
    {
        var path = paths.PluginApprovals;
        lock (_gate)
        {
            _approvals = new Dictionary<string, PluginApproval>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                if (new FileInfo(path).Length > MaxBytes)
                {
                    throw new InvalidDataException("the file is larger than it can be");
                }

                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("the file does not hold an object");
                }

                if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number > CurrentVersion)
                {
                    _leaveFileAlone = true;
                    return "plugins.json was written by a newer Hail; every plugin stays off until this Hail is updated.";
                }

                if (root.TryGetProperty("plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in plugins.EnumerateObject())
                    {
                        if (Read(entry.Value) is { } approval)
                        {
                            _approvals[entry.Name] = approval;
                        }
                    }
                }

                return null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                SetAside(path);
                return $"plugins.json could not be read ({ex.GetType().Name}); it was set aside, and every plugin is asked about again.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _leaveFileAlone = true;
                return $"plugins.json could not be read ({ex.GetType().Name}); every plugin stays off until Hail restarts.";
            }
        }
    }

    /// <summary>False when the file could not be read at startup, or a newer Hail wrote it: answers are then refused.</summary>
    public bool CanRecord
    {
        get
        {
            lock (_gate)
            {
                return !_leaveFileAlone;
            }
        }
    }

    public PluginApproval? For(string pluginId)
    {
        lock (_gate)
        {
            return _approvals.GetValueOrDefault(pluginId);
        }
    }

    /// <summary>
    /// Records the user's answer and writes the file. Throws when the disk refuses, or when the
    /// file could not be read at startup (so an answer is never written over answers not read).
    /// </summary>
    public void Record(string pluginId, PluginApproval approval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(approval);

        lock (_gate)
        {
            if (_leaveFileAlone)
            {
                throw new IOException("plugins.json could not be read when Hail started, so it is not written over.");
            }

            _approvals[pluginId] = approval;

            var plugins = new JsonObject();
            foreach (var (id, each) in _approvals.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                plugins[id] = new JsonObject { ["enabled"] = each.Enabled, ["fingerprint"] = each.Fingerprint };
            }

            var document = new JsonObject { ["version"] = CurrentVersion, ["plugins"] = plugins };
            AtomicFile.WriteAllText(paths.PluginApprovals, document.ToJsonString(WriteOptions) + Environment.NewLine);
        }
    }

    private static PluginApproval? Read(JsonElement entry) =>
        entry.ValueKind == JsonValueKind.Object
        && entry.TryGetProperty("enabled", out var enabled)
        && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False
        && entry.TryGetProperty("fingerprint", out var fingerprint)
        && fingerprint.ValueKind == JsonValueKind.String
        && Fingerprint().IsMatch(fingerprint.GetString()!)
            ? new PluginApproval(enabled.GetBoolean(), fingerprint.GetString()!)
            : null;

    private void SetAside(string path)
    {
        try
        {
            File.Move(path, Path.Combine(paths.Root, "plugins.unreadable.json"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left where it is: nothing is enabled from it, and the next answer is refused
            // rather than written over it.
            _leaveFileAlone = true;
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Fingerprint();
}
