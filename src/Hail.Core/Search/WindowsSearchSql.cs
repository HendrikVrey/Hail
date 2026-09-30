using System.Globalization;
using System.Text;

namespace Hail.Core.Search;

/// <summary>
/// Builds the one query Hail sends the Windows Search index (Hail.md §7.2), and is the only
/// way typed text reaches it.
/// </summary>
/// <remarks>
/// <para>
/// The index's SQL dialect takes no parameters, so the text is written into string literals,
/// and everything that could end a literal or change what a pattern means is escaped here:
/// single quotes doubled, the LIKE wildcards <c>%</c>, <c>_</c> and <c>[</c> bracketed, the
/// double quote and the <c>*</c> removed from a CONTAINS phrase (a phrase cannot escape them),
/// control characters dropped, and the number and length of words bounded.
/// </para>
/// <para>
/// Each word must match the file name, by its start (<c>rep</c> finds <em>report.pdf</em>) or
/// by the start of a word inside it (<c>port</c> finds <em>annual port plan.docx</em>). The
/// host's fuzzy matcher then scores and bolds what came back.
/// </para>
/// <para>
/// A word that starts with a digit is matched by word start only. Measured on 2026-09-30: the
/// index answers <c>LIKE '2pi%'</c> in about 1.9 s every time, against 17 ms for the same
/// word through CONTAINS and 20 ms for <c>LIKE 'zpi%'</c>, so a sum or a year typed into the
/// box would otherwise hold the web row back for two seconds.
/// </para>
/// </remarks>
public static class WindowsSearchSql
{
    public const int MaxTerms = 8;
    public const int MaxTermLength = 64;
    public const int MaxRows = 100;

    /// <summary>The columns selected, in this order.</summary>
    public const string PathColumn = "System.ItemPathDisplay";
    public const string NameColumn = "System.FileName";
    public const string TypeColumn = "System.ItemType";

    /// <summary>What <see cref="TypeColumn"/> holds for a folder.</summary>
    public const string FolderType = "Directory";

    /// <summary>
    /// The query for files whose names match every word of <paramref name="search"/>, best
    /// first; null when nothing searchable is left of it.
    /// </summary>
    public static string? FileNameQuery(string search, int top)
    {
        ArgumentNullException.ThrowIfNull(search);

        var terms = Terms(search);
        if (terms.Count == 0)
        {
            return null;
        }

        var sql = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"SELECT TOP {Math.Clamp(top, 1, MaxRows)} ")
            .Append(CultureInfo.InvariantCulture, $"{PathColumn}, {NameColumn}, {TypeColumn} ")
            .Append("FROM SystemIndex WHERE SCOPE='file:'");

        foreach (var term in terms)
        {
            var phrase = ContainsPhrase(term);
            var like = $"System.FileName LIKE '{LikePrefix(term)}%'";
            var contains = $"CONTAINS(System.FileName, '\"{phrase}*\"')";

            var clause = phrase.Length == 0 ? like
                : char.IsAsciiDigit(term[0]) ? contains
                : $"{like} OR {contains}";

            sql.Append(" AND (").Append(clause).Append(')');
        }

        sql.Append(" ORDER BY System.Search.Rank DESC, System.DateModified DESC");
        return sql.ToString();
    }

    /// <summary>The words searched for: split on white space, cleaned, bounded.</summary>
    public static IReadOnlyList<string> Terms(string search)
    {
        ArgumentNullException.ThrowIfNull(search);

        var clean = new string([.. search.Where(c => !char.IsControl(c))]);
        return [.. clean
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Length > MaxTermLength ? t[..MaxTermLength] : t)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTerms)];
    }

    /// <summary>A string literal's contents: a single quote doubled, as the dialect escapes it.</summary>
    public static string Literal(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("'", "''", StringComparison.Ordinal);
    }

    /// <summary>
    /// A LIKE pattern that matches <paramref name="term"/> literally: each wildcard is put in
    /// brackets, the bracket first so the brackets added are not escaped again.
    /// </summary>
    public static string LikePrefix(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        var escaped = term
            .Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);
        return Literal(escaped);
    }

    /// <summary>
    /// A CONTAINS phrase's contents. A phrase has no escape for its own quote or for the
    /// prefix star, so both are removed; what is left is a literal.
    /// </summary>
    public static string ContainsPhrase(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        var cleaned = new string([.. term.Where(c => c is not '"' and not '*')]).Trim();
        return Literal(cleaned);
    }
}
