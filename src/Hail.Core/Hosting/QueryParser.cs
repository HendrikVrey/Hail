using Hail.Sdk;

namespace Hail.Core.Hosting;

/// <summary>One provider asked one query.</summary>
public sealed record QueryTarget(ProviderRegistration Registration, int Order, Query Query);

/// <summary>What the box's text means: which providers are asked, and what each is asked.</summary>
/// <param name="RawText">Exactly what is in the box.</param>
/// <param name="Targets">The providers asked, in the user's order.</param>
/// <param name="Scope">The keyword in force and its label, or null for a global query.</param>
public sealed record ParsedQuery(string RawText, IReadOnlyList<QueryTarget> Targets, ProviderKeyword? Scope)
{
    /// <summary>The box is empty (or only spaces): nothing is asked, and history fills it instead.</summary>
    public bool IsEmpty => RawText.Trim().Length == 0;
}

/// <summary>
/// Reads the box's text into the providers it reaches (Hail.md §6.4): a keyword first sends
/// it to one provider, anything else goes to every global one.
/// </summary>
public sealed class QueryParser
{
    private readonly IReadOnlyList<ProviderRegistration> _providers;
    private readonly IReadOnlyList<(ProviderKeyword Keyword, int Order)> _keywords;

    /// <remarks>
    /// When two providers claim the same keyword the first in order keeps it; the host logs the
    /// clash when it composes the list, so this only has to be deterministic.
    /// </remarks>
    public QueryParser(IReadOnlyList<ProviderRegistration> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keywords = new List<(ProviderKeyword, int)>();
        for (var order = 0; order < providers.Count; order++)
        {
            foreach (var keyword in providers[order].Keywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword.Keyword) && !keyword.Keyword.Any(char.IsWhiteSpace) && seen.Add(keyword.Keyword))
                {
                    keywords.Add((keyword, order));
                }
            }
        }

        // Longest first, so "gh" is found before "g" would claim "gh cats" as a Google search.
        _keywords = [.. keywords.OrderByDescending(k => k.Item1.Keyword.Length)];
    }

    public IReadOnlyList<ProviderRegistration> Providers => _providers;

    /// <summary>The keywords in force, each once, for a caller that has to say which exist.</summary>
    public IEnumerable<ProviderKeyword> Keywords => _keywords.Select(k => k.Keyword);

    public ParsedQuery Parse(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        var text = rawText.TrimStart();
        foreach (var (keyword, order) in _keywords)
        {
            if (Scopes(keyword, text, out var search))
            {
                var registration = _providers[order];
                var query = new Query(rawText, search, keyword.Keyword, IsKeywordScoped: true);
                return new ParsedQuery(rawText, [new QueryTarget(registration, order, query)], keyword);
            }
        }

        var global = Query.Global(rawText);
        var targets = new List<QueryTarget>();
        for (var order = 0; order < _providers.Count; order++)
        {
            if (_providers[order].IsGlobal)
            {
                targets.Add(new QueryTarget(_providers[order], order, global));
            }
        }

        return new ParsedQuery(rawText, targets, Scope: null);
    }

    /// <summary>
    /// The keyword the user has finished typing at the start of <paramref name="text"/>, so the
    /// box can turn it into a chip: <c>g cats</c> gives Google and <c>cats</c>, <c>g </c> gives
    /// Google and nothing, <c>=2+2</c> gives the calculator and <c>2+2</c>. Null when the text
    /// does not start with one, and <paramref name="rest"/> is then the text unchanged.
    /// </summary>
    public ProviderKeyword? TakeKeyword(string text, out string rest)
    {
        ArgumentNullException.ThrowIfNull(text);

        var trimmed = text.TrimStart();
        foreach (var (keyword, _) in _keywords)
        {
            if (Scopes(keyword, trimmed, out _))
            {
                rest = trimmed[keyword.Keyword.Length..].TrimStart();
                return keyword;
            }
        }

        rest = text;
        return null;
    }

    /// <summary>The keyword in force under this spelling, or null; for a chip kept across a change of providers.</summary>
    public ProviderKeyword? FindKeyword(string keyword) =>
        _keywords.Select(k => k.Keyword).FirstOrDefault(k => string.Equals(k.Keyword, keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The text a chip and what follows it stand for, as it would be typed: <c>g cats</c>,
    /// <c>=2+2</c>. Parsing it scopes the query to the chip's provider.
    /// </summary>
    public static string Compose(ProviderKeyword keyword, string rest)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        ArgumentNullException.ThrowIfNull(rest);
        return keyword.NeedsSpace ? $"{keyword.Keyword} {rest}" : keyword.Keyword + rest;
    }

    private static bool Scopes(ProviderKeyword keyword, string text, out string search)
    {
        search = string.Empty;
        var word = keyword.Keyword;
        if (!text.StartsWith(word, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = text[word.Length..];
        if (keyword.NeedsSpace && (rest.Length == 0 || !char.IsWhiteSpace(rest[0])))
        {
            return false;
        }

        search = rest.Trim();
        return true;
    }
}
