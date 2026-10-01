using Hail.Core.Settings;

namespace Hail.Persistence.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HailTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_missing_file_is_written_with_the_defaults_and_reads_back_as_them()
    {
        var store = new SettingsStore(new HailPaths(_root));

        var first = store.Load();
        Assert.True(File.Exists(store.FilePath));
        Assert.Empty(first.Problems);

        var second = store.Load();
        Assert.Empty(second.Problems);
        Assert.Equal(HailSettings.Default.KeepLastQuery, second.Settings.KeepLastQuery);
        Assert.Equal(HailSettings.Default.WebSearch.Engines, second.Settings.WebSearch.Engines);
        Assert.Equal("g", second.Settings.WebSearch.DefaultKeyword);
    }

    [Fact]
    public void Every_field_is_read()
    {
        var load = SettingsStore.Parse("""
            {
              // Edited by hand, with a comment and a trailing comma.
              "keepLastQuery": true,
              "webSearch": {
                "defaultEngine": "d",
                "engines": [ { "keyword": "d", "name": "DuckDuckGo", "template": "https://duckduckgo.com/?q={query}" } ],
              },
              "disabledProviders": [ "hail.files" ],
            }
            """);

        Assert.Empty(load.Problems);
        Assert.True(load.Settings.KeepLastQuery);
        Assert.Equal("d", load.Settings.WebSearch.DefaultKeyword);
        Assert.Equal("DuckDuckGo", Assert.Single(load.Settings.WebSearch.Engines).Name);
        Assert.False(load.Settings.IsEnabled("hail.files"));
        Assert.True(load.Settings.IsEnabled("hail.apps"));
    }

    [Fact]
    public void A_bad_value_falls_back_alone_and_says_so()
    {
        var load = SettingsStore.Parse("""
            {
              "keepLastQuery": "yes",
              "webSearch": { "engines": [ { "keyword": "d" }, { "keyword": "x", "name": "X", "template": "https://x.example/?q={query}" } ] },
              "disabledProviders": [ "hail.files" ]
            }
            """);

        Assert.False(load.Settings.KeepLastQuery);
        Assert.Equal("X", Assert.Single(load.Settings.WebSearch.Engines).Name);
        Assert.False(load.Settings.IsEnabled("hail.files"));
        Assert.Equal(2, load.Problems.Count);
        Assert.Contains(load.Problems, p => p.Contains("keepLastQuery", StringComparison.Ordinal));
        Assert.Contains(load.Problems, p => p.Contains("entry 1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"keepLastQuery\": ")]
    public void A_file_that_cannot_be_read_gives_the_defaults(string text)
    {
        var load = SettingsStore.Parse(text);

        Assert.Single(load.Problems);
        Assert.Equal(HailSettings.Default.KeepLastQuery, load.Settings.KeepLastQuery);
        Assert.Equal(HailSettings.Default.WebSearch.Engines.Count, load.Settings.WebSearch.Engines.Count);
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        Assert.Empty(SettingsStore.Parse("""{ "theme": "dark", "shortcutSound": 5 }""").Problems);
    }

    [Fact]
    public void Lists_of_the_wrong_kind_fall_back()
    {
        var load = SettingsStore.Parse("""{ "disabledProviders": "hail.files", "webSearch": { "engines": {} } }""");

        Assert.Equal(2, load.Problems.Count);
        Assert.True(load.Settings.IsEnabled("hail.files"));
        Assert.Equal(HailSettings.Default.WebSearch.Engines.Count, load.Settings.WebSearch.Engines.Count);
    }
}
