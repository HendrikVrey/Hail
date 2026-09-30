using System.Text.RegularExpressions;
using Hail.Core.Search;

namespace Hail.Core.Tests;

/// <summary>
/// The one function that puts typed text into the Windows Search index's SQL (Hail.md §7.2),
/// held to it the way Sling's URL encoding was: whatever is typed, it stays inside its literal.
/// </summary>
public sealed partial class WindowsSearchSqlTests
{
    [Fact]
    public void A_word_becomes_a_name_prefix_and_a_word_prefix()
    {
        var sql = WindowsSearchSql.FileNameQuery("report", 40);

        Assert.Equal(
            "SELECT TOP 40 System.ItemPathDisplay, System.FileName, System.ItemType FROM SystemIndex WHERE SCOPE='file:'"
                + " AND (System.FileName LIKE 'report%' OR CONTAINS(System.FileName, '\"report*\"'))"
                + " ORDER BY System.Search.Rank DESC, System.DateModified DESC",
            sql);
    }

    [Fact]
    public void A_word_starting_with_a_digit_is_matched_by_word_start_only()
    {
        var sql = WindowsSearchSql.FileNameQuery("2025 taxes", 10)!;

        Assert.Contains("AND (CONTAINS(System.FileName, '\"2025*\"'))", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("LIKE '2025%'", sql, StringComparison.Ordinal);
        Assert.Contains("LIKE 'taxes%'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_word_must_match()
    {
        var sql = WindowsSearchSql.FileNameQuery("tax  report", 10)!;

        Assert.Contains("LIKE 'tax%'", sql, StringComparison.Ordinal);
        Assert.Contains("LIKE 'report%'", sql, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(sql, @"AND \("));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Nothing_searchable_is_no_query(string search)
    {
        Assert.Null(WindowsSearchSql.FileNameQuery(search, 10));
    }

    [Theory]
    [InlineData("'")]
    [InlineData("o'brien")]
    [InlineData("'; DROP TABLE SystemIndex; --")]
    [InlineData("x' OR '1'='1")]
    [InlineData("') OR SCOPE='file:")]
    [InlineData("\"")]
    [InlineData("a\"b*c")]
    [InlineData("%_[]")]
    [InlineData("\u0000\u001f'x")]
    [InlineData("''''")]
    [InlineData("1' OR '1'='1")]
    [InlineData("9\"*'")]
    public void Nothing_typed_leaves_its_literal(string search)
    {
        var sql = WindowsSearchSql.FileNameQuery(search, 10);
        if (sql is null)
        {
            return;
        }

        // Remove every well-formed literal; what is left must be exactly the fixed skeleton,
        // with nothing typed outside a pair of quotes.
        var skeleton = Literal().Replace(sql, "'…'");
        Assert.DoesNotContain("DROP", skeleton, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" OR '", skeleton, StringComparison.Ordinal);
        Assert.Matches(
            @"^SELECT TOP 10 System\.ItemPathDisplay, System\.FileName, System\.ItemType FROM SystemIndex WHERE SCOPE='…'( AND \((System\.FileName LIKE '…'( OR CONTAINS\(System\.FileName, '…'\))?|CONTAINS\(System\.FileName, '…'\))\))+ ORDER BY System\.Search\.Rank DESC, System\.DateModified DESC$",
            skeleton);
        Assert.DoesNotContain(sql, char.IsControl);
    }

    [Theory]
    [InlineData("100%", "100[%]")]
    [InlineData("my_file", "my[_]file")]
    [InlineData("[draft]", "[[]draft]")]
    [InlineData("it's", "it''s")]
    public void Like_wildcards_are_matched_literally(string term, string pattern)
    {
        Assert.Equal(pattern, WindowsSearchSql.LikePrefix(term));
    }

    [Theory]
    [InlineData("a\"b", "ab")]
    [InlineData("star*", "star")]
    [InlineData("it's", "it''s")]
    [InlineData("\"*\"", "")]
    public void A_contains_phrase_drops_what_it_cannot_escape(string term, string phrase)
    {
        Assert.Equal(phrase, WindowsSearchSql.ContainsPhrase(term));
    }

    [Fact]
    public void A_term_that_is_only_quotes_and_stars_keeps_its_like_and_loses_its_contains()
    {
        var sql = WindowsSearchSql.FileNameQuery("\"*", 10)!;

        Assert.Contains("LIKE '\"*%'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CONTAINS", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Words_and_their_length_are_bounded()
    {
        var terms = WindowsSearchSql.Terms(string.Join(' ', Enumerable.Range(0, 20).Select(i => i + new string('a', 100))));

        Assert.Equal(WindowsSearchSql.MaxTerms, terms.Count);
        Assert.All(terms, t => Assert.True(t.Length <= WindowsSearchSql.MaxTermLength));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(5000, WindowsSearchSql.MaxRows)]
    public void The_row_count_is_bounded(int asked, int used)
    {
        Assert.StartsWith($"SELECT TOP {used} ", WindowsSearchSql.FileNameQuery("x", asked), StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_words_are_asked_once()
    {
        Assert.Single(WindowsSearchSql.Terms("tax TAX Tax"));
    }

    /// <summary>A SQL string literal: a quote, then anything with quotes doubled, then a quote.</summary>
    [GeneratedRegex("'(?:[^']|'')*'")]
    private static partial Regex Literal();
}
