using Hail.Core.Ports;
using Hail.Core.Settings;
using Hail.Core.Updates;

namespace Hail.Persistence.Tests;

/// <summary>The settings window's writes, the fields M3 added, and the update check's own file.</summary>
public sealed class SettingsWritingTests : IDisposable
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
    public void What_is_saved_is_what_is_read_back()
    {
        var store = new SettingsStore(new HailPaths(_root));
        var settings = HailSettings.Default.WithProvider("hail.files", enabled: false) with
        {
            KeepLastQuery = true,
            Hotkey = Chord.TryParse("Ctrl+Shift+K", out _)!,
            CheckForUpdates = true,
            WebSearch = new WebSearchOptions([new("d", "DuckDuckGo", "https://duckduckgo.com/?q={query}")], "d"),
        };

        store.Save(settings);
        var load = store.Load();

        Assert.Empty(load.Problems);
        Assert.True(load.Settings.KeepLastQuery);
        Assert.Equal("Ctrl+Shift+K", load.Settings.Hotkey.Display);
        Assert.True(load.Settings.CheckForUpdates);
        Assert.False(load.Settings.IsEnabled("hail.files"));
        Assert.Equal(settings.WebSearch.Engines, load.Settings.WebSearch.Engines);
        Assert.Equal("d", load.Settings.WebSearch.DefaultKeyword);
    }

    [Fact]
    public void An_unanswered_update_question_is_not_written_as_an_answer()
    {
        var text = SettingsStore.Format(HailSettings.Default);

        Assert.DoesNotContain("checkForUpdates", text, StringComparison.Ordinal);
        Assert.Null(SettingsStore.Parse(text).Settings.CheckForUpdates);
        Assert.Contains("\"hotkey\": \"Alt+Space\"", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "hotkey": "Shift+A" }""", "Ctrl, Alt or Win")]
    [InlineData("""{ "hotkey": "Alt+;" }""", "not a key")]
    [InlineData("""{ "hotkey": 5 }""", "written like")]
    public void A_shortcut_that_cannot_be_used_falls_back_to_alt_space_and_says_why(string json, string said)
    {
        var load = SettingsStore.Parse(json);

        Assert.Equal(Chord.AltSpace, load.Settings.Hotkey);
        Assert.Contains(said, Assert.Single(load.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void No_engines_at_all_reads_back_without_a_complaint()
    {
        var text = SettingsStore.Format(HailSettings.Default with { WebSearch = new WebSearchOptions([], string.Empty) });

        Assert.DoesNotContain("defaultEngine", text, StringComparison.Ordinal);
        var load = SettingsStore.Parse(text);
        Assert.Empty(load.Problems);
        Assert.Empty(load.Settings.WebSearch.Engines);
    }

    [Fact]
    public void Checking_for_updates_is_read_as_an_answer_or_as_no_answer()
    {
        Assert.False(SettingsStore.Parse("""{ "checkForUpdates": false }""").Settings.CheckForUpdates);
        Assert.Null(SettingsStore.Parse("""{ "checkForUpdates": null }""").Settings.CheckForUpdates);
        var wrong = SettingsStore.Parse("""{ "checkForUpdates": "yes" }""");
        Assert.Null(wrong.Settings.CheckForUpdates);
        Assert.Single(wrong.Problems);
    }

    [Fact]
    public void A_save_the_disk_refuses_leaves_the_file_as_it_was()
    {
        var paths = new HailPaths(_root);
        var store = new SettingsStore(paths);
        store.Save(HailSettings.Default);
        var before = File.ReadAllText(paths.Settings);

        using (new FileStream(paths.Settings, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // A sharing violation on the final move surfaces as UnauthorizedAccessException; either is the disk refusing.
            var refused = Assert.ThrowsAny<Exception>(() => store.Save(HailSettings.Default with { KeepLastQuery = true }));
            Assert.True(refused is IOException or UnauthorizedAccessException, refused.GetType().Name);
        }

        Assert.Equal(before, File.ReadAllText(paths.Settings));
    }

    [Fact]
    public void The_update_state_round_trips()
    {
        var store = new UpdateStateStore(new HailPaths(_root));
        var state = new UpdateState(
            new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero),
            "1.1.0",
            new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero));

        Assert.True(store.Save(state));

        Assert.Equal(state, store.Load());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "lastCheckedUtc": 5, "skippedVersion": 7 }""")]
    public void An_update_state_that_cannot_be_read_is_empty(string text)
    {
        var paths = new HailPaths(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(paths.UpdateState, text);

        Assert.Equal(UpdateState.Empty, new UpdateStateStore(paths).Load());
    }
}
