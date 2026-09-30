namespace Hail.Sdk;

/// <summary>A run of characters in a title.</summary>
public readonly record struct TextSpan
{
    /// <summary>A run of <paramref name="length"/> characters from <paramref name="start"/>.</summary>
    public TextSpan(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        Start = start;
        Length = length;
    }

    /// <summary>The first character's position.</summary>
    public int Start { get; }

    /// <summary>How many characters; at least one.</summary>
    public int Length { get; }

    /// <summary>The position just after the last character.</summary>
    public int End => Start + Length;
}

/// <summary>
/// The characters of a title that matched a query, as ordered runs that do not touch or
/// overlap. Built from any positions in any order; the constructor sorts and merges them, so
/// a provider cannot hand the host a shape it has to defend against.
/// </summary>
public sealed class MatchSpans
{
    private MatchSpans(IReadOnlyList<TextSpan> spans) => Spans = spans;

    /// <summary>Nothing matched.</summary>
    public static MatchSpans Empty { get; } = new([]);

    /// <summary>The runs, in order, none touching another.</summary>
    public IReadOnlyList<TextSpan> Spans { get; }

    /// <summary>Merges character positions into runs: 0, 1, 2, 5 becomes [0, 3) and [5, 6).</summary>
    public static MatchSpans FromPositions(IEnumerable<int> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);

        var sorted = positions.Distinct().Order().ToArray();
        if (sorted.Length == 0)
        {
            return Empty;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(sorted[0], nameof(positions));

        var spans = new List<TextSpan>();
        var start = sorted[0];
        var previous = start;
        foreach (var position in sorted.Skip(1))
        {
            if (position != previous + 1)
            {
                spans.Add(new TextSpan(start, previous - start + 1));
                start = position;
            }

            previous = position;
        }

        spans.Add(new TextSpan(start, previous - start + 1));
        return new MatchSpans(spans);
    }

    /// <summary>Every position covered by a run, in order.</summary>
    public IEnumerable<int> Positions() => Spans.SelectMany(s => Enumerable.Range(s.Start, s.Length));
}

/// <summary>How well a query matched some text, and where.</summary>
/// <param name="Score">0 to 1, higher is better.</param>
/// <param name="Spans">The characters that matched.</param>
public sealed record MatchResult(double Score, MatchSpans Spans);
