using Hail.Sdk;

namespace Hail.Core.Tests;

public sealed class MatchSpansTests
{
    [Fact]
    public void Positions_merge_into_ordered_runs()
    {
        var spans = MatchSpans.FromPositions([5, 0, 2, 1, 1]);
        Assert.Equal([new TextSpan(0, 3), new TextSpan(5, 1)], spans.Spans);
        Assert.Equal([0, 1, 2, 5], spans.Positions());
    }

    [Fact]
    public void No_positions_is_empty()
    {
        Assert.Empty(MatchSpans.FromPositions([]).Spans);
    }

    [Fact]
    public void A_negative_position_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MatchSpans.FromPositions([-1, 2]));
    }

    [Fact]
    public void A_span_has_a_length()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextSpan(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextSpan(-1, 1));
    }
}
