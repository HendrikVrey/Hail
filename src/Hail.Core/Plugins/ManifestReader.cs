using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hail.Core.Plugins;

/// <summary>A manifest read, or the sentence that says why it could not be.</summary>
public sealed record ManifestRead(PluginManifest? Manifest, string? Problem)
{
    public static ManifestRead Refused(string problem) => new(null, problem);
}

/// <summary>
/// Reads <c>plugin.json</c> (Hail.md §6.2). Everything in it is checked here, before any code
/// of the plugin is loaded: a manifest either describes a plugin the host can drive or is
/// refused whole, with one sentence naming the field, because the consent the user gives is
/// given to what the manifest says.
/// </summary>
/// <remarks>
/// Unknown properties are ignored, so a manifest written for a later SDK still reads; comments
/// and trailing commas are accepted, because people write these by hand.
/// </remarks>
public static partial class ManifestReader
{
    /// <summary>Far beyond a real manifest; the caller reads no more than this.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>First-party providers' ids begin with this; a plugin may not borrow it.</summary>
    public const string ReservedPrefix = "hail.";

    public const int MaxKeywords = 4;
    public const int MaxKeywordLength = 16;
    public const int MaxSettings = 32;
    public const int MaxChoices = 32;

    /// <summary>A debounce past this would make a plugin feel broken rather than careful.</summary>
    public static readonly TimeSpan MaxDebounce = TimeSpan.FromSeconds(2);

    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 16,
    };

    /// <summary>Reads <paramref name="json"/>, the manifest found in the folder named <paramref name="folderName"/>.</summary>
    public static ManifestRead Read(string json, string folderName)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(folderName);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException ex)
        {
            return ManifestRead.Refused($"plugin.json is not valid JSON (line {ex.LineNumber + 1}).");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ManifestRead.Refused("plugin.json does not hold an object.");
            }

            try
            {
                return new ManifestRead(Parse(document.RootElement, folderName), null);
            }
            catch (ManifestException ex)
            {
                return ManifestRead.Refused(ex.Message);
            }
        }
    }

    private static PluginManifest Parse(JsonElement root, string folderName)
    {
        var id = RequiredText(root, "id", 64);
        if (!IdPattern().IsMatch(id))
        {
            throw new ManifestException("\"id\" must be lower-case words joined by dots, such as \"yourname.myplugin\".");
        }

        if (id.StartsWith(ReservedPrefix, StringComparison.Ordinal))
        {
            throw new ManifestException($"\"id\" may not begin with \"{ReservedPrefix}\", which is Hail's own.");
        }

        if (!string.Equals(id, folderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ManifestException($"The plugin's folder must be named after its id, \"{id}\".");
        }

        var sdkText = RequiredText(root, "sdk", 16);
        if (!SdkVersion.TryParse(sdkText, out var sdk))
        {
            throw new ManifestException("\"sdk\" must be the Hail.Sdk version it was built against, such as \"1.0\".");
        }

        var entry = RequiredText(root, "entry", 128);
        if (!entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || entry.IndexOfAny(['/', '\\', ':']) >= 0
            || entry.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || entry.StartsWith('.'))
        {
            throw new ManifestException("\"entry\" must be the file name of an assembly in the plugin's folder, such as \"MyPlugin.dll\".");
        }

        var typeName = RequiredText(root, "type", 512);
        if (!TypeNamePattern().IsMatch(typeName))
        {
            throw new ManifestException("\"type\" must be the provider's full type name, such as \"MyPlugin.MyProvider\".");
        }

        var debounceMs = OptionalInt(root, "debounceMs", 0);
        if (debounceMs < 0 || debounceMs > MaxDebounce.TotalMilliseconds)
        {
            throw new ManifestException($"\"debounceMs\" must be between 0 and {MaxDebounce.TotalMilliseconds:0}.");
        }

        return new PluginManifest(
            id.ToLowerInvariant(),
            RequiredText(root, "name", 64),
            RequiredText(root, "publisher", 64),
            RequiredText(root, "version", 32),
            sdk,
            entry,
            typeName,
            Keywords(root),
            OptionalBool(root, "global", fallback: true),
            TimeSpan.FromMilliseconds(debounceMs),
            OptionalText(root, "description", 280),
            Settings(root));
    }

    private static List<string> Keywords(JsonElement root)
    {
        if (!root.TryGetProperty("keywords", out var list))
        {
            return [];
        }

        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaxKeywords)
        {
            throw new ManifestException($"\"keywords\" must be a list of at most {MaxKeywords} words.");
        }

        var keywords = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            var keyword = item.ValueKind == JsonValueKind.String ? item.GetString()! : string.Empty;
            if (keyword.Length is 0 or > MaxKeywordLength || keyword.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                throw new ManifestException($"Each keyword must be 1 to {MaxKeywordLength} characters with no spaces.");
            }

            if (!keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase))
            {
                keywords.Add(keyword);
            }
        }

        return keywords;
    }

    private static List<SettingDefinition> Settings(JsonElement root)
    {
        if (!root.TryGetProperty("settings", out var list))
        {
            return [];
        }

        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaxSettings)
        {
            throw new ManifestException($"\"settings\" must be a list of at most {MaxSettings} settings.");
        }

        var settings = new List<SettingDefinition>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new ManifestException("Each entry of \"settings\" must be an object.");
            }

            var setting = Setting(item);
            if (settings.Any(s => string.Equals(s.Key, setting.Key, StringComparison.Ordinal)))
            {
                throw new ManifestException($"The setting \"{setting.Key}\" is declared twice.");
            }

            settings.Add(setting);
        }

        return settings;
    }

    private static SettingDefinition Setting(JsonElement item)
    {
        var key = RequiredText(item, "key", 32, "a setting's \"key\"");
        if (!SettingKeyPattern().IsMatch(key))
        {
            throw new ManifestException($"The setting key \"{key}\" must start with a letter and hold only letters, digits and _.");
        }

        var kind = RequiredText(item, "type", 16, $"the setting \"{key}\"'s \"type\"") switch
        {
            "text" => SettingKind.Text,
            "toggle" => SettingKind.Toggle,
            "choice" => SettingKind.Choice,
            "secret" => SettingKind.Secret,
            _ => throw new ManifestException($"The setting \"{key}\" has a \"type\" Hail does not know; it can be text, toggle, choice or secret."),
        };

        var label = RequiredText(item, "label", 64, $"the setting \"{key}\"'s \"label\"");
        var description = OptionalText(item, "description", 280);
        var choices = kind == SettingKind.Choice ? Choices(item, key) : [];

        var defaultText = string.Empty;
        var defaultToggle = false;
        if (item.TryGetProperty("default", out var fallback))
        {
            switch (kind)
            {
                case SettingKind.Toggle when fallback.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    defaultToggle = fallback.GetBoolean();
                    break;
                case SettingKind.Text when fallback.ValueKind == JsonValueKind.String && fallback.GetString()!.Length <= SettingDefinition.MaxTextLength:
                case SettingKind.Choice when fallback.ValueKind == JsonValueKind.String && choices.Contains(fallback.GetString()!, StringComparer.Ordinal):
                    defaultText = fallback.GetString()!;
                    break;
                case SettingKind.Secret:
                    throw new ManifestException($"The secret \"{key}\" cannot have a default: a secret in a manifest is not a secret.");
                default:
                    throw new ManifestException($"The setting \"{key}\" has a \"default\" that is not one of its values.");
            }
        }
        else if (kind == SettingKind.Choice)
        {
            defaultText = choices[0];
        }

        return new SettingDefinition(key, kind, label, description, defaultText, defaultToggle, choices);
    }

    private static List<string> Choices(JsonElement item, string key)
    {
        if (!item.TryGetProperty("choices", out var list)
            || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() is 0 or > MaxChoices)
        {
            throw new ManifestException($"The choice \"{key}\" needs a \"choices\" list of 1 to {MaxChoices} values.");
        }

        var choices = new List<string>();
        foreach (var choice in list.EnumerateArray())
        {
            var value = choice.ValueKind == JsonValueKind.String ? choice.GetString()! : string.Empty;
            if (value.Length is 0 or > 64 || value.Any(char.IsControl) || choices.Contains(value, StringComparer.Ordinal))
            {
                throw new ManifestException($"The choice \"{key}\" lists a value that is empty, too long or repeated.");
            }

            choices.Add(value);
        }

        return choices;
    }

    private static string RequiredText(JsonElement element, string name, int maxLength, string? described = null) =>
        OptionalText(element, name, maxLength, described)
        ?? throw new ManifestException($"{Capitalise(described ?? $"\"{name}\"")} is missing.");

    private static string? OptionalText(JsonElement element, string name, int maxLength, string? described = null)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : null;
        if (text is null || text.Length == 0 || text.Length > maxLength || text.Any(char.IsControl))
        {
            throw new ManifestException($"{Capitalise(described ?? $"\"{name}\"")} must be text of 1 to {maxLength} characters on one line.");
        }

        return text;
    }

    private static bool OptionalBool(JsonElement element, string name, bool fallback)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new ManifestException($"\"{name}\" must be true or false.");
    }

    private static int OptionalInt(JsonElement element, string name, int fallback)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw new ManifestException($"\"{name}\" must be a whole number.");
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*(\.[a-z0-9]+(-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*(\+[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeNamePattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex SettingKeyPattern();

    private sealed class ManifestException(string message) : Exception(message);
}
