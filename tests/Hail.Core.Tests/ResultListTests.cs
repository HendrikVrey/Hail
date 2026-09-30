using Hail.Core.Ranking;

namespace Hail.Core.Tests;

public sealed class ResultListTests
{
    private static string[] Ids(ResultList list) => [.. list.Items.Select(r => r.Result.Id)];

    [Fact]
    public void A_new_list_highlights_its_first_row()
    {
        var list = new ResultList();
        Assert.Equal(-1, list.SelectedIndex);
        Assert.Null(list.Selected);

        list.Replace([Fakes.Row("a"), Fakes.Row("b")]);
        Assert.Equal(0, list.SelectedIndex);
        Assert.Equal("a", list.Selected!.Result.Id);
    }

    [Fact]
    public void Movement_clamps_at_both_ends()
    {
        var list = new ResultList();
        list.Replace([Fakes.Row("a"), Fakes.Row("b"), Fakes.Row("c")]);

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

        list.Replace([Fakes.Row("a")]);
        list.Clear();
        Assert.Equal(-1, list.SelectedIndex);
    }

    [Fact]
    public void A_late_result_never_takes_the_highlighted_row()
    {
        // Hail.md §3.2: the files provider answers after the apps, with a better match. The
        // highlighted row is still the one the user was about to press Enter on.
        var app = Fakes.Row("Notepad", 0.6);
        var list = new ResultList();
        list.Replace([app]);

        var file = Fakes.Row("notepad.txt", 0.9);
        list.Merge(Ranker.Rank([app, file], 8), 8);

        Assert.Equal(["Notepad", "notepad.txt"], Ids(list));
        Assert.Same(app, list.Selected);
    }

    [Fact]
    public void Rows_above_the_highlight_stay_and_rows_below_are_ranked_again()
    {
        var a = Fakes.Row("a", 0.9);
        var b = Fakes.Row("b", 0.8);
        var c = Fakes.Row("c", 0.7);
        var d = Fakes.Row("d", 0.6);
        var list = new ResultList();
        list.Replace([a, b, c, d]);
        list.MoveDown(); // b is highlighted

        var better = Fakes.Row("better", 0.95);
        list.Merge(Ranker.Rank([a, b, c, d, better], 8), 8);

        Assert.Equal(["a", "b", "better", "c", "d"], Ids(list));
        Assert.Same(b, list.Selected);
        Assert.Equal(1, list.SelectedIndex);
    }

    [Fact]
    public void A_merge_keeps_to_the_limit()
    {
        var kept = Fakes.Row("kept", 0.1);
        var list = new ResultList();
        list.Replace([kept]);

        list.Merge(Ranker.Rank(Enumerable.Range(0, 20).Select(i => Fakes.Row($"r{i:00}", 0.9)).Append(kept), 8), 8);

        Assert.Equal(8, list.Items.Count);
        Assert.Same(kept, list.Items[0]);
    }

    [Fact]
    public void Equal_ids_from_two_providers_are_two_rows()
    {
        var mine = Fakes.Row("same", 0.5, provider: "one");
        var theirs = Fakes.Row("same", 0.5, provider: "two");
        var list = new ResultList();
        list.Replace([mine]);

        list.Merge(Ranker.Rank([mine, theirs], 8), 8);

        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public void Merging_into_an_empty_list_is_a_replace()
    {
        var list = new ResultList();
        list.Merge([Fakes.Row("a"), Fakes.Row("b")], 8);

        Assert.Equal(0, list.SelectedIndex);
        Assert.Equal(["a", "b"], Ids(list));
    }
}
