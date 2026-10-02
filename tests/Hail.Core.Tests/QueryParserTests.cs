using Hail.Core.Hosting;

namespace Hail.Core.Tests;

public sealed class QueryParserTests
{
    private readonly RecordingLog _log = new();
    private readonly QueryParser _parser;

    public QueryParserTests()
    {
        var apps = Fakes.Register("apps", ScriptedProvider.Returning(), _log);
        var calculator = Fakes.Register("calc", ScriptedProvider.Returning(), _log) with { Keywords = [new("=", "Calculator")] };
        var files = Fakes.Register("files", ScriptedProvider.Returning(), _log);
        var web = Fakes.Register("web", ScriptedProvider.Returning(), _log) with
        {
            Keywords = [new("g", "Google"), new("gh", "GitHub"), new("w", "Wikipedia")],
        };
        var secret = Fakes.Register("secret", ScriptedProvider.Returning(), _log) with { IsGlobal = false, Keywords = [new("s", "Secret")] };

        _parser = new QueryParser([apps, calculator, files, web, secret]);
    }

    [Fact]
    public void Plain_text_reaches_every_global_provider_in_order()
    {
        var parsed = _parser.Parse("notepad");

        Assert.Null(parsed.Scope);
        Assert.Equal(["apps", "calc", "files", "web"], parsed.Targets.Select(t => t.Registration.Id));
        Assert.All(parsed.Targets, t => Assert.Equal("notepad", t.Query.Search));
        Assert.All(parsed.Targets, t => Assert.False(t.Query.IsKeywordScoped));
    }

    [Fact]
    public void A_keyword_and_a_space_scope_the_query_to_one_provider()
    {
        var parsed = _parser.Parse("g  cats and dogs ");

        var target = Assert.Single(parsed.Targets);
        Assert.Equal("web", target.Registration.Id);
        Assert.Equal("cats and dogs", target.Query.Search);
        Assert.Equal("g", target.Query.Keyword);
        Assert.True(target.Query.IsKeywordScoped);
        Assert.Equal("Google", parsed.Scope!.Label);
    }

    [Fact]
    public void The_longest_keyword_wins()
    {
        var parsed = _parser.Parse("gh hail");

        Assert.Equal("gh", Assert.Single(parsed.Targets).Query.Keyword);
        Assert.Equal("GitHub", parsed.Scope!.Label);
    }

    [Theory]
    [InlineData("g")]
    [InlineData("games")]
    [InlineData("word")]
    public void A_word_that_starts_with_a_keyword_is_not_one(string text)
    {
        Assert.Null(_parser.Parse(text).Scope);
    }

    [Fact]
    public void A_keyword_with_nothing_after_it_yet_is_scoped_and_empty()
    {
        var target = Assert.Single(_parser.Parse("g ").Targets);
        Assert.Equal(string.Empty, target.Query.Search);
    }

    [Theory]
    [InlineData("=2+2", "2+2")]
    [InlineData("= 2+2", "2+2")]
    [InlineData("  =sqrt(2)", "sqrt(2)")]
    public void A_symbol_keyword_needs_no_space(string text, string search)
    {
        var target = Assert.Single(_parser.Parse(text).Targets);
        Assert.Equal("calc", target.Registration.Id);
        Assert.Equal(search, target.Query.Search);
    }

    [Fact]
    public void Keywords_match_whatever_the_case()
    {
        Assert.Equal("Google", _parser.Parse("G cats").Scope?.Label);
    }

    [Fact]
    public void A_provider_that_is_not_global_is_reached_only_by_its_keyword()
    {
        Assert.DoesNotContain(_parser.Parse("anything").Targets, t => t.Registration.Id == "secret");
        Assert.Equal("secret", Assert.Single(_parser.Parse("s anything").Targets).Registration.Id);
    }

    [Fact]
    public void The_first_provider_keeps_a_keyword_two_claim()
    {
        var first = Fakes.Register("first", ScriptedProvider.Returning(), _log) with { Keywords = [new("x", "First")] };
        var second = Fakes.Register("second", ScriptedProvider.Returning(), _log) with { Keywords = [new("x", "Second")] };

        var parsed = new QueryParser([first, second]).Parse("x y");

        Assert.Equal("first", Assert.Single(parsed.Targets).Registration.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_box_is_empty(string text)
    {
        Assert.True(_parser.Parse(text).IsEmpty);
    }

    [Theory]
    [InlineData("g ", "g", "")]
    [InlineData("g cats", "g", "cats")]
    [InlineData("  G   cats and dogs ", "g", "cats and dogs ")]
    [InlineData("gh hail", "gh", "hail")]
    [InlineData("=", "=", "")]
    [InlineData("= 2+2", "=", "2+2")]
    public void A_finished_keyword_is_taken_off_the_front(string text, string keyword, string rest)
    {
        Assert.Equal(keyword, _parser.TakeKeyword(text, out var left)?.Keyword);
        Assert.Equal(rest, left);
    }

    [Theory]
    [InlineData("g")]
    [InlineData("gh")]
    [InlineData("games")]
    [InlineData("cats g ")]
    [InlineData("")]
    public void Text_without_a_finished_keyword_is_left_alone(string text)
    {
        Assert.Null(_parser.TakeKeyword(text, out var left));
        Assert.Equal(text, left);
    }

    [Theory]
    [InlineData("g", "cats", "g cats")]
    [InlineData("g", "", "g ")]
    [InlineData("=", "2+2", "=2+2")]
    public void A_chip_and_its_text_compose_back_into_what_would_be_typed(string keyword, string rest, string typed)
    {
        var chip = _parser.FindKeyword(keyword)!;
        var composed = QueryParser.Compose(chip, rest);

        Assert.Equal(typed, composed);
        Assert.Equal(chip, _parser.Parse(composed).Scope);
    }

    [Fact]
    public void A_keyword_no_provider_answers_to_is_not_found()
    {
        Assert.Null(_parser.FindKeyword("yt"));
        Assert.Equal("Google", _parser.FindKeyword("G")?.Label);
    }

    [Fact]
    public void The_raw_text_is_kept_exactly()
    {
        Assert.Equal("  g cats", _parser.Parse("  g cats").Targets[0].Query.RawText);
    }
}
