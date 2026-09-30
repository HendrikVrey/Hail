using Hail.Core.History;

namespace Hail.Core.Tests;

public sealed class UsageHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly UsageKey Vlc = new("hail.apps", "vlc");
    private static readonly UsageKey Studio = new("hail.apps", "studio");

    [Theory]
    [InlineData("  VS   Code ", "vs code")]
    [InlineData("", "")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", "abcdefghijklmnop")]
    [InlineData("my bank account password", "my bank account")]
    public void What_is_kept_of_a_query_is_short_and_lower_case(string typed, string kept)
    {
        Assert.Equal(kept, UsageHistory.Normalise(typed));
    }

    [Fact]
    public void A_pick_for_a_prefix_counts_for_what_extends_it_and_the_other_way()
    {
        var history = new UsageHistory();
        history.Record("v", Vlc, Now);
        history.Record("studio", Studio, Now);

        Assert.True(history.Lift("vs", Vlc, Now) > 0);
        Assert.True(history.Lift("stu", Studio, Now) > 0);
        Assert.Equal(0, history.Lift("calc", Vlc, Now));
        Assert.Equal(0, history.Lift("v", Studio, Now));
    }

    [Fact]
    public void More_picks_count_more_but_never_reach_one()
    {
        var history = new UsageHistory();
        history.Record("v", Vlc, Now);
        var once = history.Lift("v", Vlc, Now);

        for (var i = 0; i < 15; i++)
        {
            history.Record("v", Vlc, Now);
        }

        var often = history.Lift("v", Vlc, Now);
        Assert.InRange(once, 0.3, 0.5);
        Assert.True(often > once);
        Assert.True(often < 1);
    }

    [Fact]
    public void An_old_pick_counts_less()
    {
        var recent = new UsageHistory();
        recent.Record("v", Vlc, Now);
        var old = new UsageHistory();
        old.Record("v", Vlc, Now - TimeSpan.FromDays(28));

        Assert.True(old.Lift("v", Vlc, Now) < recent.Lift("v", Vlc, Now) / 2);
    }

    [Fact]
    public void Picks_past_the_age_limit_are_forgotten()
    {
        var history = new UsageHistory();
        history.Record("v", Vlc, Now - UsageHistory.MaxAge - TimeSpan.FromDays(1));
        history.Record("x", Studio, Now);

        Assert.Equal(1, history.Count);
        Assert.Equal(0, history.Lift("v", Vlc, Now));
    }

    [Fact]
    public void The_top_is_what_is_picked_most_and_most_recently()
    {
        var history = new UsageHistory();
        history.Record("s", Studio, Now - TimeSpan.FromDays(60));
        history.Record("s", Studio, Now - TimeSpan.FromDays(60));
        history.Record("v", Vlc, Now);
        history.Record("v", Vlc, Now);

        Assert.Equal([Vlc, Studio], history.Top(8, Now));
        Assert.Equal([Vlc], history.Top(1, Now));
    }

    [Fact]
    public void Each_result_keeps_its_newest_uses_only()
    {
        var history = new UsageHistory();
        for (var i = 0; i < UsageHistory.MaxUsesPerEntry + 10; i++)
        {
            history.Record("v", Vlc, Now);
        }

        Assert.Equal(UsageHistory.MaxUsesPerEntry, history.ToSnapshot().Entries.Single().Uses.Count);
    }

    [Fact]
    public void Past_the_cap_the_weakest_results_go()
    {
        var history = new UsageHistory();
        history.Record("old", new UsageKey("p", "weakest"), Now - TimeSpan.FromDays(100));
        for (var i = 0; i < UsageHistory.MaxEntries; i++)
        {
            history.Record("x", new UsageKey("p", $"r{i}"), Now);
        }

        Assert.Equal(UsageHistory.MaxEntries, history.Count);
        Assert.DoesNotContain(history.ToSnapshot().Entries, e => e.ResultId == "weakest");
    }

    [Fact]
    public void Forget_and_clear_say_they_changed_something()
    {
        var history = new UsageHistory();
        history.Record("v", Vlc, Now);
        var changes = 0;
        history.Changed += () => changes++;

        history.Forget([new UsageKey("p", "never-there")]);
        Assert.Equal(0, changes);

        history.Forget([Vlc]);
        Assert.Equal(1, changes);
        Assert.Equal(0, history.Count);

        history.Clear();
        Assert.Equal(2, changes);
    }

    [Fact]
    public void A_snapshot_round_trips()
    {
        var history = new UsageHistory();
        history.Record("v", Vlc, Now);
        history.Record("stu", Studio, Now - TimeSpan.FromDays(3));

        var copy = UsageHistory.FromSnapshot(history.ToSnapshot(), Now);

        Assert.Equal(history.Lift("v", Vlc, Now), copy.Lift("v", Vlc, Now));
        Assert.Equal(history.Top(8, Now), copy.Top(8, Now));
    }

    [Fact]
    public void A_snapshot_from_disk_is_cleaned_not_trusted()
    {
        var snapshot = new UsageSnapshot(
        [
            null!,
            new UsageEntry("", "no provider", [new UsageUse("x", Now)]),
            new UsageEntry("p", new string('x', UsageHistory.MaxIdLength + 1), [new UsageUse("x", Now)]),
            new UsageEntry("p", "no uses", []),
            new UsageEntry("p", "null uses", null!),
            new UsageEntry("p", "future", [new UsageUse("x", Now + TimeSpan.FromDays(400))]),
            new UsageEntry("p", "long query", [new UsageUse(new string('Q', 500), Now)]),
        ]);

        var history = UsageHistory.FromSnapshot(snapshot, Now);
        var kept = history.ToSnapshot().Entries;

        Assert.Equal(["future", "long query"], kept.Select(e => e.ResultId));
        Assert.All(kept.SelectMany(e => e.Uses), u => Assert.True(u.At <= Now));
        Assert.All(kept.SelectMany(e => e.Uses), u => Assert.True(u.Query.Length <= UsageHistory.MaxQueryLength));
    }

    [Fact]
    public void Nothing_is_read_from_no_snapshot()
    {
        Assert.Equal(0, UsageHistory.FromSnapshot(null, Now).Count);
    }
}
