using Hail.Core.Ports;
using Hail.Providers.Web;

namespace Hail.Providers.Tests;

/// <summary>The checks an edited list of engines passes before the settings window saves it.</summary>
public sealed class WebEngineListTests
{
    private static readonly Dictionary<string, string> NothingTaken = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void The_shipped_engines_pass() =>
        Assert.Null(WebEngineList.Check(WebSearchOptions.Default.Engines, WebSearchOptions.Default.DefaultKeyword, NothingTaken));

    [Fact]
    public void No_engines_at_all_is_allowed() =>
        Assert.Null(WebEngineList.Check([], null, NothingTaken));

    [Theory]
    [InlineData("", "g", "https://www.google.com/search?q={query}", "needs a name")]
    [InlineData("Google", "", "https://www.google.com/search?q={query}", "needs a keyword")]
    [InlineData("Google", "g g", "https://www.google.com/search?q={query}", "letters or digits")]
    [InlineData("Google", "=", "https://www.google.com/search?q={query}", "letters or digits")]
    [InlineData("Google", "abcdefghijklm", "https://www.google.com/search?q={query}", "letters or digits")]
    [InlineData("Google", "g", "http://www.google.com/search?q={query}", "https://")]
    [InlineData("Google", "g", "https://www.google.com/search?q=", "{query}")]
    [InlineData("Google", "g", "https://{query}.example.com/", "path or query")]
    public void An_engine_that_cannot_be_used_is_refused_with_its_name(string name, string keyword, string template, string said)
    {
        var problem = WebEngineList.Check([new(keyword, name, template)], keyword, NothingTaken);

        Assert.NotNull(problem);
        Assert.Contains(said, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_engines_cannot_share_a_keyword_in_any_case()
    {
        var problem = WebEngineList.Check(
            [new("g", "Google", "https://www.google.com/search?q={query}"), new("G", "Other", "https://other.example/?q={query}")],
            "g",
            NothingTaken);

        Assert.Contains("Two engines use the keyword", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_keyword_another_provider_answers_to_is_refused_and_named()
    {
        var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["e"] = "Everything" };

        var problem = WebEngineList.Check([new("E", "Ecosia", "https://www.ecosia.org/search?q={query}")], "E", taken);

        Assert.Equal("\"E\" is already Everything's keyword.", problem);
    }

    [Fact]
    public void The_last_row_needs_an_engine_that_is_in_the_list()
    {
        var engines = new List<WebEngineSetting> { new("g", "Google", "https://www.google.com/search?q={query}") };

        Assert.NotNull(WebEngineList.Check(engines, "ddg", NothingTaken));
        Assert.NotNull(WebEngineList.Check(engines, null, NothingTaken));
        Assert.Null(WebEngineList.Check(engines, "G", NothingTaken));
    }

    [Fact]
    public void There_is_a_ceiling_on_how_many()
    {
        var many = Enumerable.Range(0, WebEngineList.MaxEngines + 1)
            .Select(i => new WebEngineSetting($"k{i}", $"Engine {i}", "https://example.com/?q={query}"))
            .ToList();

        Assert.Contains("at most", WebEngineList.Check(many, "k0", NothingTaken), StringComparison.Ordinal);
    }
}
