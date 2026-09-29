using Hail.Core.Matching;

namespace Hail.Core.Tests;

public sealed class FuzzyMatcherTests
{
    private static readonly FuzzyMatcher Matcher = new();

    private static double Score(string query, string candidate) =>
        Matcher.Match(query, candidate)?.Score ?? throw new Xunit.Sdk.XunitException($"'{query}' did not match '{candidate}'.");

    private static int[] Bold(string query, string candidate) =>
        Matcher.Match(query, candidate)!.Spans.Positions().ToArray();

    [Theory]
    [InlineData("xyz", "Visual Studio Code")]
    [InlineData("edoc", "Code")]
    [InlineData("codes", "Code")]
    [InlineData("", "Code")]
    [InlineData("   ", "Code")]
    [InlineData("code", "")]
    public void Refuses_what_is_not_there_in_order(string query, string candidate)
    {
        Assert.Null(Matcher.Match(query, candidate));
    }

    [Fact]
    public void An_exact_match_scores_one_and_nothing_else_ties_it()
    {
        Assert.Equal(1.0, Score("code", "Code"));
        Assert.True(Score("code", "Code Insiders") < 1.0);
        Assert.True(Score("cod", "Code") < 1.0);
    }

    [Fact]
    public void Case_is_ignored()
    {
        Assert.Equal(Score("CODE", "code editor"), Score("code", "Code Editor"), 6);
    }

    [Fact]
    public void Acronyms_find_word_starts()
    {
        Assert.Equal([0, 7, 14], Bold("vsc", "Visual Studio Code"));
    }

    [Fact]
    public void An_acronym_beats_letters_scattered_through_a_word()
    {
        Assert.True(Score("vsc", "Visual Studio Code") > Score("vsc", "Obviously Scary"));
    }

    [Fact]
    public void A_word_start_beats_the_middle_of_a_word()
    {
        Assert.True(Score("code", "Visual Studio Code") > Score("code", "Encoder"));
    }

    [Fact]
    public void The_start_of_a_name_beats_a_later_word_start()
    {
        // Found on a real Start menu: "ps" put a VLC shortcut above PowerShell.
        Assert.True(Score("ps", "PowerShell 7 (x64)") > Score("ps", "VLC media player skinned"));
        Assert.True(Score("word", "Word 2016") > Score("word", "Windows Memory Diagnostic"));
    }

    [Fact]
    public void Camel_humps_count_as_word_starts()
    {
        Assert.Equal([0, 5], Bold("pt", "PowerToys"));
        Assert.True(Score("pt", "PowerToys") > Score("pt", "Openstep"));
    }

    [Fact]
    public void The_best_alignment_is_chosen_not_the_first()
    {
        // The first 's' in "Visual" would do, but the word start in "Studio" scores better.
        Assert.Equal([0, 7], Bold("vs", "Visual Studio"));
    }

    [Fact]
    public void Consecutive_characters_are_preferred_over_gaps()
    {
        Assert.True(Score("note", "Notepad") > Score("note", "No Time Estimate"));
        Assert.Equal([0, 1, 2, 3], Bold("note", "Notepad"));
    }

    [Fact]
    public void A_shorter_candidate_covering_more_of_itself_wins_a_tie()
    {
        Assert.True(Score("paint", "Paint") > Score("paint", "Paint 3D Remix Edition"));
        Assert.True(Score("pain", "Paint") > Score("pain", "Paint 3D Remix Edition"));
    }

    [Fact]
    public void Every_word_of_the_query_must_match_in_any_order()
    {
        Assert.NotNull(Matcher.Match("code studio", "Visual Studio Code"));
        Assert.Null(Matcher.Match("code python", "Visual Studio Code"));
        Assert.Equal([7, 8, 9, 10, 11, 12, 14, 15, 16, 17], Bold("code studio", "Visual Studio Code"));
    }

    [Fact]
    public void Scores_stay_inside_zero_and_one()
    {
        var candidates = new[] { "a", "ab", "a b c d e f g", "zzzzzzzzzzzzzzzzzzzza", "Microsoft Paint", "A" };
        foreach (var candidate in candidates)
        {
            var match = Matcher.Match("a", candidate);
            Assert.NotNull(match);
            Assert.InRange(match.Score, 0.0, 1.0);
        }
    }

    [Fact]
    public void Long_text_is_bounded_and_does_not_throw()
    {
        var longCandidate = new string('x', 10_000) + "code";
        var longQuery = new string('x', 1_000);

        // Characters past the cap are never matched, so the "code" at the end is not found.
        Assert.Null(Matcher.Match("code", longCandidate));
        Assert.NotNull(Matcher.Match(longQuery, longCandidate));
    }

    [Fact]
    public void Positions_are_inside_the_candidate()
    {
        var match = Matcher.Match("stc", "Visual Studio Code")!;
        Assert.All(match.Spans.Positions(), p => Assert.InRange(p, 0, "Visual Studio Code".Length - 1));
    }
}
