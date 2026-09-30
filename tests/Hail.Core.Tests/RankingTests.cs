using Hail.Core.Ranking;
using Hail.Sdk;

namespace Hail.Core.Tests;

public sealed class RankingTests
{
    private static string[] Ids(IEnumerable<ProviderResult> results) => [.. results.Select(r => r.Result.Id)];

    [Fact]
    public void Relevance_orders_first_then_provider_then_title_then_id()
    {
        var ranked = Ranker.Rank(
            [
                Fakes.Row("b", 0.5, order: 1),
                Fakes.Row("a", 0.5, order: 1),
                Fakes.Row("z", 0.5, order: 0),
                Fakes.Row("top", 0.9, order: 2),
                Fakes.At(Fakes.Result("a", 0.5, id: "a-second"), 1),
            ],
            limit: 10);

        Assert.Equal(["top", "z", "a", "a-second", "b"], Ids(ranked));
    }

    [Fact]
    public void The_limit_is_the_number_of_rows()
    {
        var many = Enumerable.Range(0, 20).Select(i => Fakes.Row($"r{i:00}"));
        Assert.Equal(8, Ranker.Rank(many, 8).Count);
    }

    [Theory]
    [InlineData(double.NaN, 0.0)]
    [InlineData(-3.0, 0.0)]
    [InlineData(7.0, 1.0)]
    [InlineData(0.4, 0.4)]
    public void A_providers_relevance_is_clamped_not_trusted(double given, double used)
    {
        Assert.Equal(used, Ranker.Relevance(Fakes.Result("x", given)));
    }

    [Fact]
    public void A_nan_relevance_sinks_rather_than_poisoning_the_order()
    {
        var ranked = Ranker.Rank([Fakes.Row("nan", double.NaN), Fakes.Row("low", 0.1), Fakes.Row("high", 0.9)], limit: 3);

        Assert.Equal(["high", "low", "nan"], Ids(ranked));
    }

    [Fact]
    public void History_reorders_close_matches()
    {
        var ranked = Ranker.Rank(
            [Fakes.Row("Visual Studio", 0.62), Fakes.Row("VLC", 0.6)],
            limit: 2,
            history: r => r.Result.Id == "VLC" ? 0.4 : 0);

        Assert.Equal(["VLC", "Visual Studio"], Ids(ranked));
    }

    [Fact]
    public void History_cannot_lift_a_poor_match_over_a_good_one()
    {
        // At most twice the relevance, so 0.3 can never beat 0.7.
        var ranked = Ranker.Rank(
            [Fakes.Row("good", 0.7), Fakes.Row("poor", 0.3)],
            limit: 2,
            history: r => r.Result.Id == "poor" ? 1.0 : 0);

        Assert.Equal(["good", "poor"], Ids(ranked));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-5.0)]
    [InlineData(99.0)]
    public void A_history_lift_outside_its_range_is_clamped(double lift)
    {
        var ranked = Ranker.Rank([Fakes.Row("a", 0.4), Fakes.Row("b", 0.9)], limit: 2, history: _ => lift);
        Assert.Equal("b", ranked[0].Result.Id);
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.02, true)]
    [InlineData(0.021, false)]
    [InlineData(0.5, false)]
    public void A_last_resort_is_a_relevance_at_or_below_the_floor(double relevance, bool lastResort)
    {
        Assert.Equal(lastResort, Ranker.IsLastResort(Fakes.Result("x", relevance)));
    }
}
