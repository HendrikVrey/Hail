using Hail.Core.Ports;
using Hail.Providers.Web;
using Hail.Sdk;
using static Hail.Providers.Tests.Harness;

namespace Hail.Providers.Tests;

public sealed class WebSearchTests
{
    private readonly FakeLauncher _launcher = new();
    private readonly FakeClipboard _clipboard = new();

    private async Task<WebSearchProvider> StartedAsync(WebSearchOptions? options = null)
    {
        var provider = new WebSearchProvider(options ?? WebSearchOptions.Default);
        Assert.Empty(provider.Load());
        await provider.InitializeAsync(Context(_launcher, _clipboard), Token);
        return provider;
    }

    [Theory]
    [InlineData("https://www.google.com/search?q={query}")]
    [InlineData("https://en.wikipedia.org/wiki/{query}")]
    [InlineData("https://example.com/a/{query}/b?x=1")]
    public void A_template_with_the_search_in_its_path_or_query_is_accepted(string template)
    {
        Assert.NotNull(WebTemplate.TryCreate(template, out _));
    }

    [Theory]
    [InlineData(null, "empty")]
    [InlineData("", "empty")]
    [InlineData("http://example.com/?q={query}", "https")]
    [InlineData("https://example.com/?q=", "exactly once")]
    [InlineData("https://example.com/?q={query}&r={query}", "exactly once")]
    [InlineData("https://{query}.example.com/", "server")]
    [InlineData("https://{query}@example.com/", "server")]
    [InlineData("https://example.com/#{query}", "path or query")]
    [InlineData("javascript:alert({query})", "https")]
    [InlineData("file:///C:/{query}", "https")]
    [InlineData("not a url {query}", "web address")]
    public void Any_other_template_is_refused_with_the_reason(string? template, string reason)
    {
        Assert.Null(WebTemplate.TryCreate(template, out var problem));
        Assert.Contains(reason, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_search_is_encoded_whole()
    {
        var template = WebTemplate.TryCreate("https://example.com/search?q={query}", out _)!;

        var uri = template.UriFor("a b&c=d/?#x");

        Assert.Equal("https://example.com/search?q=a%20b%26c%3Dd%2F%3F%23x", uri.AbsoluteUri);
        Assert.Equal("example.com", uri.Host);
    }

    [Fact]
    public async Task Without_a_keyword_it_offers_a_last_resort_with_the_default_engine()
    {
        var result = Assert.Single(await CollectAsync(await StartedAsync(), Query.Global("cats")));

        Assert.True(result.Relevance <= Result.LastResort);
        Assert.Contains("Google", result.Title, StringComparison.Ordinal);

        await Run(result.Primary);
        Assert.Equal(["uri https://www.google.com/search?q=cats"], _launcher.Calls);
    }

    [Fact]
    public async Task A_keyword_picks_the_engine_and_ranks_it_first()
    {
        var result = Assert.Single(await CollectAsync(await StartedAsync(), Scoped("yt", "lo-fi beats")));

        Assert.Equal(1.0, result.Relevance);
        Assert.Contains("YouTube", result.Title, StringComparison.Ordinal);

        await Run(On(result, Gesture.CtrlC));
        Assert.Equal(["text https://www.youtube.com/results?search_query=lo-fi%20beats"], _clipboard.Calls);
    }

    [Fact]
    public async Task A_keyword_with_nothing_after_it_invites_a_search_and_opens_nothing()
    {
        var result = Assert.Single(await CollectAsync(await StartedAsync(), Scoped("g", "")));

        Assert.Equal(ActionOutcome.KeepOpen, await Run(result.Primary));
        Assert.Empty(_launcher.Calls);
    }

    [Theory]
    [InlineData(@"C:\Users")]
    [InlineData(@"~\Documents")]
    [InlineData(@"\\server\share")]
    [InlineData("")]
    public async Task A_path_or_an_empty_box_is_not_searched_for(string text)
    {
        Assert.Empty(await CollectAsync(await StartedAsync(), Query.Global(text)));
    }

    [Fact]
    public void A_bad_engine_is_left_out_with_a_reason_and_the_rest_are_kept()
    {
        var provider = new WebSearchProvider(new WebSearchOptions(
            [
                new("g", "Google", "https://www.google.com/search?q={query}"),
                new("x", "Evil", "http://evil.example/?q={query}"),
                new("g", "Second Google", "https://google.example/?q={query}"),
                new("", "Nameless", "https://example.com/?q={query}"),
            ],
            DefaultKeyword: "missing"));

        var problems = provider.Load();

        Assert.Equal(3, problems.Count);
        Assert.Equal([("g", "Google")], provider.Keywords);
    }

    [Fact]
    public async Task A_default_that_does_not_exist_falls_back_to_the_first_engine()
    {
        var provider = await StartedAsync(new WebSearchOptions([new("d", "DuckDuckGo", "https://duckduckgo.com/?q={query}")], DefaultKeyword: "nope"));

        Assert.Contains("DuckDuckGo", Assert.Single(await CollectAsync(provider, Query.Global("x"))).Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_searched_for_is_remembered()
    {
        Assert.False(typeof(IRecall).IsAssignableFrom(typeof(WebSearchProvider)));
    }
}
