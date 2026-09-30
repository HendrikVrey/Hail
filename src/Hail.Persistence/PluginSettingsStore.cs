using System.Text.Json;
using System.Text.Json.Nodes;
using Hail.Core.Plugins;

namespace Hail.Persistence;

/// <summary>
/// The values of every plugin's settings (<c>plugin-settings.json</c>), one object per plugin
/// id. A secret is stored as the bytes DPAPI encrypted for the account (<c>{"protected":
/// "base64"}</c>), never as text. Thread-safe.
/// </summary>
/// <remarks>
/// Values are read against the plugin's declarations each time they are asked for, so a value
/// of the wrong kind, or a choice no longer offered, falls back to its default alone; values
/// for a plugin that is not installed today are kept in the file untouched.
/// </remarks>
public sealed class PluginSettingsStore(HailPaths paths)
{
    public const int MaxBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly Lock _gate = new();
    private JsonObject _root = [];
    private bool _leaveFileAlone;

    /// <summary>Reads the file; a sentence for the log when it could not be used.</summary>
    public string? Load()
    {
        var path = paths.PluginSettings;
        lock (_gate)
        {
            _root = [];
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

                _root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                    ?? throw new InvalidDataException("the file does not hold an object");
                return null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // Not set aside: it may hold secrets, which belong where the user put them.
                _leaveFileAlone = true;
                return $"plugin-settings.json could not be read ({ex.GetType().Name}); plugins use their defaults and nothing is saved until Hail restarts.";
            }
        }
    }

    /// <summary>What the user has set for <paramref name="pluginId"/>, read against its declarations.</summary>
    public IReadOnlyDictionary<string, SettingValue> ValuesFor(string pluginId, IReadOnlyList<SettingDefinition> schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(schema);

        var values = new Dictionary<string, SettingValue>(StringComparer.Ordinal);
        lock (_gate)
        {
            if (_root[pluginId] is not JsonObject stored)
            {
                return values;
            }

            foreach (var setting in schema)
            {
                if (Read(stored[setting.Key]) is { } value && setting.Accepts(value))
                {
                    values[setting.Key] = value;
                }
            }
        }

        return values;
    }

    /// <summary>Writes <paramref name="values"/> as <paramref name="pluginId"/>'s. Throws when the disk refuses.</summary>
    public void Save(string pluginId, IReadOnlyDictionary<string, SettingValue> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(values);

        var stored = new JsonObject();
        foreach (var (key, value) in values.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            JsonNode? node = value switch
            {
                SettingValue.Text text => text.Value,
                SettingValue.Toggle toggle => toggle.Value,
                SettingValue.ProtectedSecret secret => new JsonObject { ["protected"] = Convert.ToBase64String(secret.Protected) },
                _ => null,
            };

            if (node is not null)
            {
                stored[key] = node;
            }
        }

        lock (_gate)
        {
            if (_leaveFileAlone)
            {
                throw new IOException("plugin-settings.json could not be read when Hail started, so it is not written over.");
            }

            _root[pluginId] = stored;
            AtomicFile.WriteAllText(paths.PluginSettings, _root.ToJsonString(WriteOptions) + Environment.NewLine);
        }
    }

    private static SettingValue? Read(JsonNode? node)
    {
        switch (node?.GetValueKind())
        {
            case JsonValueKind.String:
                return new SettingValue.Text(node.GetValue<string>());
            case JsonValueKind.True or JsonValueKind.False:
                return new SettingValue.Toggle(node.GetValue<bool>());
            case JsonValueKind.Object when node["protected"] is JsonValue encoded && encoded.GetValueKind() == JsonValueKind.String:
                var bytes = new byte[SettingDefinition.MaxTextLength * 4];
                return Convert.TryFromBase64String(encoded.GetValue<string>(), bytes, out var written)
                    ? new SettingValue.ProtectedSecret(bytes[..written])
                    : null;
            default:
                return null;
        }
    }
}
