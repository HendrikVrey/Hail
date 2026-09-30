using System.Text.Json;
using System.Text.Json.Nodes;
using Hail.Core.Ports;
using Hail.Core.Settings;

namespace Hail.Persistence;

/// <summary>What reading the settings file found, and what in it was not used.</summary>
/// <param name="Problems">One sentence per value that fell back to its default; for the log.</param>
public sealed record SettingsLoad(HailSettings Settings, IReadOnlyList<string> Problems);

/// <summary>
/// Reads <c>settings.json</c> field by field (Hail.md §10.4): a value of the wrong kind falls
/// back to its default alone, with a line saying so, and the rest of the file still counts. A
/// missing file is written with the defaults, so there is something to open and edit.
/// </summary>
/// <remarks>
/// The file accepts comments and trailing commas, because people edit it by hand until the
/// settings window arrives in M3.
/// </remarks>
public sealed class SettingsStore(HailPaths paths)
{
    /// <summary>Far beyond any real settings file; anything larger is not read.</summary>
    public const int MaxBytes = 1024 * 1024;

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 16,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string FilePath => paths.Settings;

    public SettingsLoad Load()
    {
        var path = paths.Settings;
        if (!File.Exists(path))
        {
            WriteDefaults(path);
            return new SettingsLoad(HailSettings.Default, []);
        }

        string text;
        try
        {
            if (new FileInfo(path).Length > MaxBytes)
            {
                return new SettingsLoad(HailSettings.Default, ["settings.json is larger than a settings file can be; the defaults were used."]);
            }

            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsLoad(HailSettings.Default, [$"settings.json could not be read ({ex.GetType().Name}); the defaults were used."]);
        }

        return Parse(text);
    }

    /// <summary>Reads settings from text; the whole of the reading, so a test needs no disk.</summary>
    public static SettingsLoad Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, ReadOptions);
        }
        catch (JsonException ex)
        {
            return new SettingsLoad(HailSettings.Default, [$"settings.json is not valid JSON (line {ex.LineNumber + 1}); the defaults were used."]);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new SettingsLoad(HailSettings.Default, ["settings.json does not hold an object; the defaults were used."]);
            }

            var problems = new List<string>();
            var defaults = HailSettings.Default;
            var settings = new HailSettings(
                ReadBool(root, "keepLastQuery", defaults.KeepLastQuery, problems),
                ReadWebSearch(root, defaults.WebSearch, problems),
                ReadStringSet(root, "disabledProviders", defaults.DisabledProviders, problems));

            return new SettingsLoad(settings, problems);
        }
    }

    private static bool ReadBool(JsonElement root, string name, bool fallback, List<string> problems)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        problems.Add($"\"{name}\" must be true or false; the default was used.");
        return fallback;
    }

    private static IReadOnlySet<string> ReadStringSet(JsonElement root, string name, IReadOnlySet<string> fallback, List<string> problems)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
        {
            problems.Add($"\"{name}\" must be a list of names; the default was used.");
            return fallback;
        }

        return value.EnumerateArray().Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
    }

    private static WebSearchOptions ReadWebSearch(JsonElement root, WebSearchOptions fallback, List<string> problems)
    {
        if (!root.TryGetProperty("webSearch", out var web))
        {
            return fallback;
        }

        if (web.ValueKind != JsonValueKind.Object)
        {
            problems.Add("\"webSearch\" must be an object; the default engines were used.");
            return fallback;
        }

        var defaultKeyword = fallback.DefaultKeyword;
        if (web.TryGetProperty("defaultEngine", out var chosen))
        {
            if (chosen.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(chosen.GetString()))
            {
                defaultKeyword = chosen.GetString()!.Trim();
            }
            else
            {
                problems.Add("\"webSearch.defaultEngine\" must be an engine's keyword; the default was used.");
            }
        }

        var engines = fallback.Engines;
        if (web.TryGetProperty("engines", out var list))
        {
            if (list.ValueKind == JsonValueKind.Array)
            {
                var read = new List<WebEngineSetting>();
                var index = 0;
                foreach (var entry in list.EnumerateArray())
                {
                    var engine = ReadEngine(entry);
                    if (engine is null)
                    {
                        problems.Add($"\"webSearch.engines\" entry {index + 1} needs a keyword, a name and a template; it was left out.");
                    }
                    else
                    {
                        read.Add(engine);
                    }

                    index++;
                }

                engines = read;
            }
            else
            {
                problems.Add("\"webSearch.engines\" must be a list; the default engines were used.");
            }
        }

        return new WebSearchOptions(engines, defaultKeyword);
    }

    private static WebEngineSetting? ReadEngine(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var keyword = Text(entry, "keyword");
        var name = Text(entry, "name");
        var template = Text(entry, "template");
        return keyword is null || name is null || template is null ? null : new WebEngineSetting(keyword, name, template);

        static string? Text(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;
    }

    private static void WriteDefaults(string path)
    {
        var defaults = HailSettings.Default;
        var document = new JsonObject
        {
            ["keepLastQuery"] = defaults.KeepLastQuery,
            ["webSearch"] = new JsonObject
            {
                ["defaultEngine"] = defaults.WebSearch.DefaultKeyword,
                ["engines"] = new JsonArray([.. defaults.WebSearch.Engines.Select(e => (JsonNode)new JsonObject
                {
                    ["keyword"] = e.Keyword,
                    ["name"] = e.Name,
                    ["template"] = e.Template,
                })]),
            },
            ["disabledProviders"] = new JsonArray(),
        };

        try
        {
            AtomicFile.WriteAllText(path, document.ToJsonString(WriteOptions) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the file Hail still runs on its defaults; the next start tries again.
        }
    }
}
