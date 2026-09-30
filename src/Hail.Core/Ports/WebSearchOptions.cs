namespace Hail.Core.Ports;

/// <summary>A web search engine as the settings file names it, not yet checked.</summary>
/// <param name="Keyword">What is typed first to use it: <c>g</c> in <c>g cats</c>.</param>
/// <param name="Name">What the box calls it.</param>
/// <param name="Template">
/// An https address with <c>{query}</c> where the search goes, in its path or query string.
/// </param>
public sealed record WebEngineSetting(string Keyword, string Name, string Template);

/// <summary>The engines the web search provider offers, and which one the fallback row uses.</summary>
public sealed record WebSearchOptions(IReadOnlyList<WebEngineSetting> Engines, string DefaultKeyword)
{
    /// <summary>What Hail ships with (Hail.md §7.4).</summary>
    public static WebSearchOptions Default { get; } = new(
        [
            new("g", "Google", "https://www.google.com/search?q={query}"),
            new("ddg", "DuckDuckGo", "https://duckduckgo.com/?q={query}"),
            new("b", "Bing", "https://www.bing.com/search?q={query}"),
            new("yt", "YouTube", "https://www.youtube.com/results?search_query={query}"),
            new("gh", "GitHub", "https://github.com/search?q={query}"),
            new("w", "Wikipedia", "https://en.wikipedia.org/wiki/Special:Search?search={query}"),
        ],
        DefaultKeyword: "g");
}
