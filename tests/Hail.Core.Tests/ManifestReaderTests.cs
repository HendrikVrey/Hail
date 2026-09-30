using System.Text.Json.Nodes;
using Hail.Core.Plugins;

namespace Hail.Core.Tests;

/// <summary>
/// plugin.json read before any code of the plugin is loaded (Hail.md §6.2): a manifest either
/// describes something the host can drive, or is refused whole with one sentence naming the
/// field, because the user's consent is given to what it says.
/// </summary>
public sealed class ManifestReaderTests
{
    [Fact]
    public void A_complete_manifest_reads_whole()
    {
        var read = Read(Valid(m => m["settings"] = new JsonArray(
            new JsonObject { ["key"] = "matchPath", ["type"] = "toggle", ["label"] = "Match paths", ["default"] = true },
            new JsonObject { ["key"] = "max", ["type"] = "choice", ["label"] = "Results", ["choices"] = new JsonArray("20", "50"), ["default"] = "50" },
            new JsonObject { ["key"] = "token", ["type"] = "secret", ["label"] = "Token", ["description"] = "From your account page." },
            new JsonObject { ["key"] = "base", ["type"] = "text", ["label"] = "Address" })));

        var manifest = Assert.IsType<PluginManifest>(read.Manifest);
        Assert.Equal("vrey.everything", manifest.Id);
        Assert.Equal("Everything", manifest.Name);
        Assert.Equal("Hendrik Vrey", manifest.Publisher);
        Assert.Equal(new SdkVersion(1, 0), manifest.Sdk);
        Assert.Equal(["e"], manifest.Keywords);
        Assert.False(manifest.IsGlobal);
        Assert.Equal(TimeSpan.FromMilliseconds(120), manifest.Debounce);
        Assert.Equal([SettingKind.Toggle, SettingKind.Choice, SettingKind.Secret, SettingKind.Text], manifest.Settings.Select(s => s.Kind));
        Assert.True(manifest.Settings[0].DefaultToggle);
        Assert.Equal("50", manifest.Settings[1].DefaultText);
        Assert.Equal(string.Empty, manifest.Settings[3].DefaultText);
    }

    [Fact]
    public void Comments_trailing_commas_and_unknown_properties_are_accepted()
    {
        var json = Valid().ToJsonString().TrimEnd('}') + ", \"icon\": \"everything.png\", // a later SDK's field\n}";

        Assert.NotNull(Read(json).Manifest);
    }

    [Fact]
    public void A_plugin_answers_every_query_unless_it_says_otherwise()
    {
        Assert.True(Read(Valid(m => m.Remove("global"))).Manifest!.IsGlobal);
    }

    [Theory]
    [InlineData("id", "Everything", "lower-case words joined by dots")]
    [InlineData("id", "nodot", "lower-case words joined by dots")]
    [InlineData("id", "hail.files", "Hail's own")]
    [InlineData("sdk", "1", "Hail.Sdk version")]
    [InlineData("sdk", "one.zero", "Hail.Sdk version")]
    [InlineData("entry", "..\\evil.dll", "file name of an assembly")]
    [InlineData("entry", "C:\\evil.dll", "file name of an assembly")]
    [InlineData("entry", "plugin.exe", "file name of an assembly")]
    [InlineData("type", "Not a type", "full type name")]
    [InlineData("name", "", "\"name\" must be text")]
    public void A_bad_value_is_refused_naming_its_field(string field, string value, string said)
    {
        var read = Read(Valid(m => m[field] = value), folder: field == "id" ? value : "vrey.everything");

        Assert.Null(read.Manifest);
        Assert.Contains(said, read.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("publisher")]
    [InlineData("version")]
    [InlineData("sdk")]
    [InlineData("entry")]
    [InlineData("type")]
    public void A_missing_field_is_refused_by_name(string field)
    {
        var read = Read(Valid(m => m.Remove(field)));

        Assert.Equal($"\"{field}\" is missing.", read.Problem);
    }

    [Fact]
    public void The_folder_must_be_named_after_the_id()
    {
        Assert.Contains("named after its id", Read(Valid(), folder: "elsewhere").Problem, StringComparison.Ordinal);
        Assert.NotNull(Read(Valid(), folder: "VREY.Everything").Manifest);
    }

    [Theory]
    [InlineData("[\"has space\"]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"a\",\"b\",\"c\",\"d\",\"e\"]")]
    [InlineData("\"e\"")]
    public void Keywords_must_be_a_short_list_of_single_words(string keywords)
    {
        Assert.Null(Read(Valid(m => m["keywords"] = JsonNode.Parse(keywords))).Manifest);
    }

    [Fact]
    public void A_debounce_past_two_seconds_is_refused()
    {
        Assert.Contains("between 0 and 2000", Read(Valid(m => m["debounceMs"] = 2001)).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_cannot_have_a_default()
    {
        var read = Read(Valid(m => m["settings"] = new JsonArray(
            new JsonObject { ["key"] = "token", ["type"] = "secret", ["label"] = "Token", ["default"] = "abc" })));

        Assert.Contains("a secret in a manifest is not a secret", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_choice_needs_choices_and_a_default_among_them()
    {
        Assert.Contains("needs a \"choices\" list", Read(Setting(new JsonObject { ["key"] = "c", ["type"] = "choice", ["label"] = "C" })).Problem, StringComparison.Ordinal);
        Assert.Contains("not one of its values", Read(Setting(new JsonObject { ["key"] = "c", ["type"] = "choice", ["label"] = "C", ["choices"] = new JsonArray("a"), ["default"] = "b" })).Problem, StringComparison.Ordinal);
        Assert.Equal("a", Read(Setting(new JsonObject { ["key"] = "c", ["type"] = "choice", ["label"] = "C", ["choices"] = new JsonArray("a", "b") })).Manifest!.Settings[0].DefaultText);
    }

    [Fact]
    public void A_setting_declared_twice_is_refused()
    {
        var read = Read(Valid(m => m["settings"] = new JsonArray(
            new JsonObject { ["key"] = "a", ["type"] = "toggle", ["label"] = "A" },
            new JsonObject { ["key"] = "a", ["type"] = "text", ["label"] = "A again" })));

        Assert.Contains("declared twice", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_setting_of_an_unknown_kind_is_refused()
    {
        Assert.Contains("text, toggle, choice or secret", Read(Setting(new JsonObject { ["key"] = "a", ["type"] = "colour", ["label"] = "A" })).Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    public void Something_that_is_not_an_object_is_refused(string json)
    {
        Assert.NotNull(ManifestReader.Read(json, "vrey.everything").Problem);
    }

    [Theory]
    [InlineData(1, 0, 1, 0, null)]
    [InlineData(1, 0, 1, 3, null)]
    [InlineData(1, 4, 1, 3, "newer Hail")]
    [InlineData(2, 0, 1, 3, "newer Hail")]
    [InlineData(0, 2, 1, 0, "no longer speaks")]
    public void A_plugin_runs_on_its_own_major_up_to_the_hosts_minor(int pluginMajor, int pluginMinor, int hostMajor, int hostMinor, string? said)
    {
        var problem = SdkCompatibility.Problem(new SdkVersion(pluginMajor, pluginMinor), new SdkVersion(hostMajor, hostMinor));

        if (said is null)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.Contains(said, problem, StringComparison.Ordinal);
        }
    }

    private static ManifestRead Read(JsonObject manifest, string folder = "vrey.everything") => ManifestReader.Read(manifest.ToJsonString(), folder);

    private static ManifestRead Read(string json) => ManifestReader.Read(json, "vrey.everything");

    private static JsonObject Setting(JsonObject setting) => Valid(m => m["settings"] = new JsonArray(setting));

    private static JsonObject Valid(Action<JsonObject>? edit = null)
    {
        var manifest = new JsonObject
        {
            ["id"] = "vrey.everything",
            ["name"] = "Everything",
            ["publisher"] = "Hendrik Vrey",
            ["version"] = "1.0.0",
            ["sdk"] = "1.0",
            ["entry"] = "Hail.Plugin.Everything.dll",
            ["type"] = "Hail.Plugin.Everything.EverythingProvider",
            ["keywords"] = new JsonArray("e"),
            ["global"] = false,
            ["debounceMs"] = 120,
        };
        edit?.Invoke(manifest);
        return manifest;
    }
}
