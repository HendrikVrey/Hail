using Hail.Core.Ranking;

namespace Hail.Core.Tests;

public sealed class RankingTests
{
    [Fact]
    public void Relevance_orders_first_then_provider_then_title_then_id()
    {
        var ranked = Ranker.Rank(
            [
                new(Fakes.Result("b", 0.5), 1),
                new(Fakes.Result("a", 0.5), 1),
                new(Fakes.Result("z", 0.5), 0),
                new(Fakes.Result("top", 0.9), 2),
                new(Fakes.Result("a", 0.5, id: "a-second"), 1),
            ],
            limit: 10);

        Assert.Equal(["top", "z", "a", "a-second", "b"], ranked.Select(r => r.Id));
    }

    [Fact]
    public void The_limit_is_the_number_of_rows()
    {
        var many = Enumerable.Range(0, 20).Select(i => new ProviderResult(Fakes.Result($"r{i:00}"), 0));
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
        var ranked = Ranker.Rank(
            [new(Fakes.Result("nan", double.NaN), 0), new(Fakes.Result("low", 0.1), 0), new(Fakes.Result("high", 0.9), 0)],
            limit: 3);

        Assert.Equal(["high", "low", "nan"], ranked.Select(r => r.Id));
    }

    [Fact]
    public void A_new_list_highlights_its_first_row()
    {
        var list = new ResultList();
        Assert.Equal(-1, list.SelectedIndex);
        Assert.Null(list.Selected);

        list.Replace([Fakes.Result("a"), Fakes.Result("b")]);
        Assert.Equal(0, list.SelectedIndex);
        Assert.Equal("a", list.Selected!.Id);
    }

    [Fact]
    public void Movement_clamps_at_both_ends()
    {
        var list = new ResultList();
        list.Replace([Fakes.Result("a"), Fakes.Result("b"), Fakes.Result("c")]);

        list.MoveUp();
        Assert.Equal(0, list.SelectedIndex);

        list.MoveDown();
        list.MoveDown();
        list.MoveDown();
        Assert.Equal(2, list.SelectedIndex);

        list.Select(-5);
        Assert.Equal(0, list.SelectedIndex);
    }

    [Fact]
    public void An_empty_list_has_nothing_to_move()
    {
        var list = new ResultList();
        list.MoveDown();
        list.Select(3);
        Assert.Equal(-1, list.SelectedIndex);

        list.Replace([Fakes.Result("a")]);
        list.Clear();
        Assert.Equal(-1, list.SelectedIndex);
    }
}
