using Hail.Sdk;

namespace Hail.Plugin.Everything.Tests;

/// <summary>The provider over a fake Everything: what it asks, what it offers, and what it says when it cannot.</summary>
public sealed class EverythingProviderTests
{
    [Fact]
    public async Task What_everything_finds_is_offered_ranked_by_the_hosts_matcher()
    {
        var everything = new FakeEverything(
            new EverythingItem("report.docx", @"C:\Docs", IsFolder: false, IsDrive: false),
            new EverythingItem("old", @"C:\Reports 2024", IsFolder: true, IsDrive: false));
        var provider = await ProviderAsync(everything);

        var results = await QueryAsync(provider, "report");

        var file = Assert.Single(results, r => r.Title == "report.docx");
        Assert.Equal(@"C:\Docs\report.docx", file.Id);
        Assert.Equal(@"C:\Docs", file.Subtitle);
        Assert.Equal(["Show in folder", "Copy path", "Copy file"], file.Secondary.Select(a => a.Title));

        // Found by Everything's own rules (the folder's name), not by the name: offered low.
        var folder = Assert.Single(results, r => r.Title == "old");
        Assert.True(folder.Relevance < file.Relevance);
        Assert.Equal(["Copy path"], folder.Secondary.Select(a => a.Title));
    }

    [Fact]
    public async Task The_settings_decide_what_everything_is_asked()
    {
        var everything = new FakeEverything();
        var provider = await ProviderAsync(everything, matchPath: true, maxResults: "20");

        await QueryAsync(provider, "cats");

        Assert.Equal(("cats", EverythingIpc.MatchPath, 20), everything.Asked.Single());
    }

    [Fact]
    public async Task A_network_path_is_not_offered()
    {
        var provider = await ProviderAsync(new FakeEverything(new EverythingItem("share.txt", @"\\server\share", false, false)));

        Assert.Empty(await QueryAsync(provider, "share"));
    }

    [Fact]
    public async Task When_everything_is_not_running_the_box_says_so()
    {
        var provider = await ProviderAsync(new FakeEverything { Failure = new EverythingUnavailableException("Everything is not running.") });

        var notice = Assert.Single(await QueryAsync(provider, "cats"));

        Assert.Equal("Everything is not running.", notice.Title);
    }

    [Fact]
    public async Task One_letter_is_not_sent()
    {
        var everything = new FakeEverything();
        var provider = await ProviderAsync(everything);

        Assert.Empty(await QueryAsync(provider, "c"));
        Assert.Empty(everything.Asked);
    }

    [Fact]
    public async Task The_keyword_alone_offers_what_was_picked_most_that_still_exists()
    {
        var here = Path.Combine(Path.GetTempPath(), $"hail-everything-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(here, "x", TestContext.Current.CancellationToken);
        try
        {
            var everything = new FakeEverything();
            var provider = await ProviderAsync(everything, picked: [here, @"C:\gone\nowhere.txt"]);

            var result = Assert.Single(await QueryAsync(provider, string.Empty));

            Assert.Equal(here, result.Id);
            Assert.Empty(everything.Asked);
        }
        finally
        {
            File.Delete(here);
        }
    }

    [Fact]
    public async Task A_remembered_file_is_gone_only_when_its_drive_is_there()
    {
        var provider = await ProviderAsync(new FakeEverything());

        Assert.IsType<Recollection.GoneRecollection>(await provider.RecallAsync(@"C:\surely\not\here.txt", TestContext.Current.CancellationToken));
        Assert.IsType<Recollection.UnknownRecollection>(await provider.RecallAsync(@"\\server\share\a.txt", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Disposing_the_provider_closes_everythings_reply_window()
    {
        var everything = new FakeEverything();
        new EverythingProvider(everything).Dispose();

        Assert.True(everything.Disposed);
    }

    private static async Task<EverythingProvider> ProviderAsync(FakeEverything everything, bool matchPath = false, string maxResults = "50", string[]? picked = null)
    {
        var provider = new EverythingProvider(everything);
        await provider.InitializeAsync(new Context(matchPath, maxResults, picked ?? []), TestContext.Current.CancellationToken);
        return provider;
    }

    private static async Task<List<Result>> QueryAsync(EverythingProvider provider, string search)
    {
        var results = new List<Result>();
        await foreach (var result in provider.QueryAsync(new Query("e " + search, search, "e", IsKeywordScoped: true), TestContext.Current.CancellationToken))
        {
            results.Add(result);
        }

        return results;
    }

    private sealed class FakeEverything(params EverythingItem[] items) : IEverything
    {
        public List<(string Search, uint Flags, int Max)> Asked { get; } = [];

        public Exception? Failure { get; init; }

        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<EverythingItem>> SearchAsync(string search, uint flags, int maxResults, CancellationToken ct)
        {
            Asked.Add((search, flags, maxResults));
            return Failure is null ? Task.FromResult<IReadOnlyList<EverythingItem>>(items) : Task.FromException<IReadOnlyList<EverythingItem>>(Failure);
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>A context with a real-enough matcher: a prefix of the name matches, and nothing else does.</summary>
    private sealed class Context(bool matchPath, string maxResults, string[] picked) : IPluginContext, IPluginSettings, IPluginHistory, IMatcher
    {
        public ILauncher Launcher => throw new NotSupportedException();

        public IMatcher Matcher => this;

        public IClipboard Clipboard => throw new NotSupportedException();

        public IPluginLog Log { get; } = new NullLog();

        public IPluginSettings Settings => this;

        public IPluginHistory History => this;

        public string DataFolder => throw new NotSupportedException();

        public MatchResult? Match(string query, string candidate) =>
            candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                ? new MatchResult(0.9, MatchSpans.FromPositions(Enumerable.Range(0, query.Length)))
                : null;

        public string GetText(string key) => throw new ArgumentException(key);

        public string GetChoice(string key) => key == "maxResults" ? maxResults : throw new ArgumentException(key);

        public bool GetToggle(string key) => key == "matchPath" ? matchPath : throw new ArgumentException(key);

        public string? GetSecret(string key) => throw new ArgumentException(key);

        public IReadOnlyList<string> MostPicked(int count) => [.. picked.Take(count)];
    }

    private sealed class NullLog : IPluginLog
    {
        public void LogInfo(string message)
        {
        }

        public void LogError(string message, Exception? exception = null)
        {
        }
    }
}
