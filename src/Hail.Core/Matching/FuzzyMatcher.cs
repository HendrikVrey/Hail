using Hail.Sdk;

namespace Hail.Core.Matching;

/// <summary>
/// A fuzzy matcher in the fzf family (Hail.md §6.6): the query's characters must appear in
/// the candidate in order, and the alignment chosen is the one that scores best, rewarding
/// word starts, camel-case humps and consecutive runs and charging for gaps. That is what
/// makes <c>vsc</c> find <em>Visual Studio Code</em> and <c>code</c> prefer <em>Code</em>
/// over <em>Encoder</em>.
/// </summary>
/// <remarks>
/// <para>
/// Words in the query are matched separately and must all match, in any order, so
/// <c>code studio</c> finds <em>Visual Studio Code</em>.
/// </para>
/// <para>
/// The alignment is a dynamic programme over query by candidate, linear in each with an
/// affine gap carried along the row, so its cost is bounded by the two length limits below
/// and never by the shape of the text. Stateless and safe to share between threads.
/// </para>
/// </remarks>
public sealed class FuzzyMatcher : IMatcher
{
    /// <summary>Longest query word considered; the rest of a longer word is not matched.</summary>
    public const int MaxTermLength = 64;

    /// <summary>Longest candidate considered; characters past it are never matched.</summary>
    public const int MaxCandidateLength = 256;

    // fzf's constants, which have survived a decade of people typing into them, plus one of
    // Hail's: the very start of a name is worth more than any later word start, because a
    // launcher is asked for names by their beginnings ("ps" is PowerShell before it is
    // "VLC media player skinned").
    private const int ScoreMatch = 16;
    private const int BonusAtStart = 12;
    private const int ScoreGapStart = -3;
    private const int ScoreGapExtension = -1;
    private const int BonusAfterWhitespace = 10;
    private const int BonusAfterDelimiter = 9;
    private const int BonusCamel = 7;
    private const int BonusConsecutive = 4;
    private const int FirstCharMultiplier = 2;

    /// <summary>How much of the score rewards covering more of the candidate.</summary>
    private const double CoverageWeight = 0.1;

    private const int Unreachable = int.MinValue / 4;

    public MatchResult? Match(string query, string candidate)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidate);

        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0 || candidate.Length == 0)
        {
            return null;
        }

        var text = candidate.Length > MaxCandidateLength ? candidate[..MaxCandidateLength] : candidate;

        if (terms.Length == 1 && string.Equals(terms[0], text, StringComparison.OrdinalIgnoreCase))
        {
            return new MatchResult(1.0, MatchSpans.FromPositions(Enumerable.Range(0, text.Length)));
        }

        var bonuses = Bonuses(text);
        var positions = new HashSet<int>();
        var total = 0.0;

        foreach (var rawTerm in terms)
        {
            var term = rawTerm.Length > MaxTermLength ? rawTerm[..MaxTermLength] : rawTerm;
            var aligned = Align(term, text, bonuses);
            if (aligned is null)
            {
                return null;
            }

            total += aligned.Value.Normalized;
            positions.UnionWith(aligned.Value.Positions);
        }

        var quality = total / terms.Length;
        var coverage = (double)positions.Count / text.Length;
        var score = ((1 - CoverageWeight) * quality) + (CoverageWeight * coverage);

        // An exact match is 1.0 and nothing else may tie it.
        return new MatchResult(Math.Clamp(score, 0.001, 0.999), MatchSpans.FromPositions(positions));
    }

    private static (double Normalized, int[] Positions)? Align(string term, string text, int[] bonuses)
    {
        var m = term.Length;
        var n = text.Length;
        if (m > n || !IsSubsequence(term, text))
        {
            return null;
        }

        var score = new int[m, n];
        var chunkBonus = new int[m, n];
        var previous = new int[m, n];

        for (var j = 0; j < n; j++)
        {
            if (Same(term[0], text[j]))
            {
                score[0, j] = ScoreMatch + (bonuses[j] * FirstCharMultiplier);
                chunkBonus[0, j] = bonuses[j];
            }
            else
            {
                score[0, j] = Unreachable;
            }

            previous[0, j] = -1;
        }

        for (var i = 1; i < m; i++)
        {
            // The best way to arrive at column j after a gap: the previous query character
            // matched at some k <= j - 2, charged a start and an extension per extra skip.
            var gapBest = Unreachable;
            var gapFrom = -1;

            for (var j = 0; j < n; j++)
            {
                if (j >= 2)
                {
                    var extended = gapBest + ScoreGapExtension;
                    var started = score[i - 1, j - 2] + ScoreGapStart;
                    if (started >= extended)
                    {
                        gapBest = started;
                        gapFrom = j - 2;
                    }
                    else
                    {
                        gapBest = extended;
                    }
                }

                score[i, j] = Unreachable;
                previous[i, j] = -1;

                if (!Same(term[i], text[j]))
                {
                    continue;
                }

                if (j >= 1 && score[i - 1, j - 1] > Unreachable)
                {
                    // A consecutive run keeps the bonus of the character that started it, so
                    // "stu" in "Studio" scores as a word start all the way along.
                    var run = chunkBonus[i - 1, j - 1];
                    var startsNewChunk = bonuses[j] >= BonusAfterDelimiter && bonuses[j] > run;
                    var bonus = startsNewChunk ? bonuses[j] : Math.Max(bonuses[j], Math.Max(BonusConsecutive, run));
                    score[i, j] = score[i - 1, j - 1] + ScoreMatch + bonus;
                    chunkBonus[i, j] = startsNewChunk ? bonuses[j] : Math.Max(run, BonusConsecutive);
                    previous[i, j] = j - 1;
                }

                if (gapBest > Unreachable)
                {
                    var viaGap = gapBest + ScoreMatch + bonuses[j];
                    if (viaGap > score[i, j])
                    {
                        score[i, j] = viaGap;
                        chunkBonus[i, j] = bonuses[j];
                        previous[i, j] = gapFrom;
                    }
                }
            }
        }

        var best = Unreachable;
        var end = -1;
        for (var j = 0; j < n; j++)
        {
            if (score[m - 1, j] > best)
            {
                best = score[m - 1, j];
                end = j;
            }
        }

        if (end < 0)
        {
            return null;
        }

        var positions = new int[m];
        for (int i = m - 1, j = end; i >= 0; i--)
        {
            positions[i] = j;
            j = previous[i, j];
        }

        // The best a term of this length could ever score: every character consecutive from
        // the very start of the text.
        var ceiling = (ScoreMatch * m) + (BonusAtStart * FirstCharMultiplier) + (BonusAtStart * (m - 1));
        var normalized = Math.Clamp((double)best / ceiling, 0.01, 1.0);
        return (normalized, positions);
    }

    private static bool IsSubsequence(string term, string text)
    {
        var i = 0;
        foreach (var c in text)
        {
            if (Same(term[i], c) && ++i == term.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Same(char a, char b) => a == b || char.ToLowerInvariant(a) == char.ToLowerInvariant(b);

    /// <summary>What matching each character of the text is worth, from what precedes it.</summary>
    private static int[] Bonuses(string text)
    {
        var bonuses = new int[text.Length];
        for (var j = 0; j < text.Length; j++)
        {
            var current = text[j];
            if (!char.IsLetterOrDigit(current))
            {
                continue;
            }

            if (j == 0)
            {
                bonuses[j] = BonusAtStart;
                continue;
            }

            if (char.IsWhiteSpace(text[j - 1]))
            {
                bonuses[j] = BonusAfterWhitespace;
                continue;
            }

            var before = text[j - 1];
            if (!char.IsLetterOrDigit(before))
            {
                bonuses[j] = BonusAfterDelimiter;
            }
            else if ((char.IsLower(before) && char.IsUpper(current)) || (!char.IsDigit(before) && char.IsDigit(current)))
            {
                bonuses[j] = BonusCamel;
            }
        }

        return bonuses;
    }
}
