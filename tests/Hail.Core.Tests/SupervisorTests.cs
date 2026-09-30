using System.Runtime.CompilerServices;
using Hail.Core.History;
using Hail.Core.Hosting;
using Hail.Sdk;

namespace Hail.Core.Tests;

public sealed class SupervisorTests
{
    private readonly RecordingLog _log = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SupervisorOptions Options(int firstFrameMs = 1000, int hardMs = 3000) =>
        new() { FirstFrameBudget = TimeSpan.FromMilliseconds(firstFrameMs), HardBudget = TimeSpan.FromMilliseconds(hardMs) };

    private ProviderRegistration Register(string id, IProvider provider, int debounceMs = 0) =>
        Fakes.Register(id, provider, _log) with { Debounce = TimeSpan.FromMilliseconds(debounceMs) };

    private static Task<List<QueryUpdate>> RunAsync(Supervisor supervisor, string text) => RunUntilAsync(supervisor, text, Token);

    private static async Task<List<QueryUpdate>> RunUntilAsync(Supervisor supervisor, string text, CancellationToken ct)
    {
        var parsed = new QueryParser(supervisor.Providers).Parse(text);
        var updates = new List<QueryUpdate>();
        await foreach (var update in supervisor.RunAsync(parsed, ct))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static string[] Ids(QueryUpdate update) => [.. update.Results.Select(r => r.Result.Id).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Results_carry_their_providers_place_and_id()
    {
        var supervisor = new Supervisor(
            [Register("first", ScriptedProvider.Returning(Fakes.Result("a"))), Register("second", ScriptedProvider.Returning(Fakes.Result("b")))],
            _log,
            Options());

        var final = (await RunAsync(supervisor, "x"))[^1];

        Assert.True(final.IsComplete);
        Assert.Equal(
            [("a", 0, "first"), ("b", 1, "second")],
            final.Results.Select(r => (r.Result.Id, r.ProviderOrder, r.ProviderId)).OrderBy(r => r.ProviderOrder));
    }

    [Fact]
    public async Task The_first_update_waits_for_every_provider_answering_from_memory()
    {
        var slowish = new ScriptedProvider((_, ct) => Delayed(30, ct, Fakes.Result("slowish")));
        var supervisor = new Supervisor([Register("fast", ScriptedProvider.Returning(Fakes.Result("fast"))), Register("slowish", slowish)], _log, Options());

        var updates = await RunAsync(supervisor, "x");

        var only = Assert.Single(updates);
        Assert.True(only.IsComplete);
        Assert.Equal(["fast", "slowish"], Ids(only));
    }

    [Fact]
    public async Task A_debounced_provider_joins_in_a_later_update()
    {
        var supervisor = new Supervisor(
            [Register("apps", ScriptedProvider.Returning(Fakes.Result("app"))), Register("files", ScriptedProvider.Returning(Fakes.Result("file")), debounceMs: 50)],
            _log,
            Options());

        var updates = await RunAsync(supervisor, "x");

        Assert.Equal(2, updates.Count);
        Assert.Equal(["app"], Ids(updates[0]));
        Assert.False(updates[0].IsComplete);
        Assert.Equal(["app", "file"], Ids(updates[1]));
        Assert.True(updates[1].IsComplete);
    }

    [Fact]
    public async Task A_slow_provider_does_not_hold_the_first_frame_past_its_budget()
    {
        var slow = new ScriptedProvider((_, ct) => Delayed(400, ct, Fakes.Result("slow")));
        var supervisor = new Supervisor([Register("fast", ScriptedProvider.Returning(Fakes.Result("fast"))), Register("slow", slow)], _log, Options(firstFrameMs: 40));

        var updates = await RunAsync(supervisor, "x");

        Assert.Equal(["fast"], Ids(updates[0]));
        Assert.Equal(["fast", "slow"], Ids(updates[^1]));
        Assert.True(updates[^1].IsComplete);
    }

    [Fact]
    public async Task A_provider_is_initialised_once_across_queries()
    {
        var provider = ScriptedProvider.Returning(Fakes.Result("a"));
        var supervisor = new Supervisor([Register("p", provider)], _log, Options());

        await RunAsync(supervisor, "x");
        await RunAsync(supervisor, "y");

        Assert.Equal(1, provider.Initialisations);
    }

    [Fact]
    public async Task A_provider_that_throws_costs_itself_and_not_the_others()
    {
        var supervisor = new Supervisor(
            [Register("broken", new ScriptedProvider((_, _) => Throwing())), Register("fine", ScriptedProvider.Returning(Fakes.Result("ok")))],
            _log,
            Options());

        var final = (await RunAsync(supervisor, "secret words"))[^1];

        Assert.Equal(["ok"], Ids(final));
        var line = Assert.Single(_log.Errors(), l => l.Contains("broken", StringComparison.Ordinal));
        Assert.Contains("12 characters", line, StringComparison.Ordinal);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_provider_that_fails_to_start_is_switched_off_and_said_once()
    {
        var provider = new ScriptedProvider((_, ct) => ScriptedProvider.Yield([Fakes.Result("never")], ct))
        {
            OnInitialize = () => throw new InvalidOperationException("no"),
        };
        var supervisor = new Supervisor([Register("p", provider)], _log, Options());

        Assert.Empty((await RunAsync(supervisor, "a"))[^1].Results);
        Assert.Empty((await RunAsync(supervisor, "b"))[^1].Results);

        Assert.Single(_log.Errors(), l => l.Contains("failed to start", StringComparison.Ordinal));
        Assert.Equal(1, provider.Initialisations);
        Assert.True(supervisor.IsDisabled("p"));
        Assert.Equal("p", Assert.Single(supervisor.Faults).ProviderId);
    }

    [Fact]
    public async Task Cancelling_ends_the_stream_quietly()
    {
        var supervisor = new Supervisor([Register("endless", new ScriptedProvider((_, ct) => Endless(ct)))], _log, Options(firstFrameMs: 10));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunUntilAsync(supervisor, "x", cancel.Token));

        Assert.Empty(_log.Errors());
        Assert.False(supervisor.IsDisabled("endless"));
    }

    [Fact]
    public async Task A_provider_that_ignores_cancellation_is_abandoned_at_the_hard_budget()
    {
        var stubborn = new ScriptedProvider((_, _) => Ignoring());
        var supervisor = new Supervisor(
            [Register("stubborn", stubborn), Register("fine", ScriptedProvider.Returning(Fakes.Result("ok")))],
            _log,
            Options(firstFrameMs: 20, hardMs: 150));

        var started = DateTime.UtcNow;
        var final = (await RunAsync(supervisor, "x"))[^1];

        Assert.True(final.IsComplete);
        Assert.Equal(["ok"], Ids(final));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
        Assert.Contains(_log.Lines, l => l.Contains("stubborn faulted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Three_faults_in_a_minute_switch_a_provider_off_until_restart()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        var broken = new ScriptedProvider((_, _) => Throwing());
        var supervisor = new Supervisor([Register("broken", broken)], _log, Options(), time);
        var raised = 0;
        supervisor.FaultsChanged += () => raised++;

        for (var i = 0; i < 3; i++)
        {
            await RunAsync(supervisor, "x");
            time.Now += TimeSpan.FromSeconds(10);
        }

        Assert.True(supervisor.IsDisabled("broken"));
        Assert.Equal(1, raised);
        Assert.Contains("3 times in a minute", Assert.Single(supervisor.Faults).Reason, StringComparison.Ordinal);

        await RunAsync(supervisor, "y");
        Assert.Equal(3, broken.Queries);
    }

    [Fact]
    public async Task Faults_further_apart_than_the_window_do_not_add_up()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        var supervisor = new Supervisor([Register("flaky", new ScriptedProvider((_, _) => Throwing()))], _log, Options(), time);

        for (var i = 0; i < 5; i++)
        {
            await RunAsync(supervisor, "x");
            time.Now += TimeSpan.FromSeconds(31);
        }

        Assert.False(supervisor.IsDisabled("flaky"));
    }

    [Fact]
    public async Task A_flood_is_cut_off_at_the_cap()
    {
        var supervisor = new Supervisor([Register("flood", new ScriptedProvider((_, ct) => Endless(ct, delay: false)))], _log, Options() with { MaxResultsPerProvider = 250 });

        var final = (await RunAsync(supervisor, "x"))[^1];

        Assert.Equal(250, final.Results.Count);
    }

    [Fact]
    public async Task Malformed_results_are_dropped()
    {
        var noTitle = Fakes.Result("x") with { Title = " " };
        var noId = Fakes.Result("y") with { Id = "" };
        var nullSecondary = Fakes.Result("z") with { Secondary = null! };
        var supervisor = new Supervisor([Register("p", ScriptedProvider.Returning(noTitle, noId, nullSecondary, Fakes.Result("good")))], _log, Options());

        Assert.Equal(["good"], Ids((await RunAsync(supervisor, "x"))[^1]));
    }

    [Fact]
    public async Task A_debounced_provider_is_not_asked_when_typing_goes_on()
    {
        var files = ScriptedProvider.Returning(Fakes.Result("file"));
        var supervisor = new Supervisor([Register("files", files, debounceMs: 300)], _log, Options(firstFrameMs: 10));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(60));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunUntilAsync(supervisor, "x", cancel.Token));
        await Task.Delay(400, Token);

        Assert.Equal(0, files.Queries);
    }

    [Fact]
    public async Task No_providers_is_one_complete_empty_update()
    {
        var supervisor = new Supervisor([], _log, Options());

        var only = Assert.Single(await RunAsync(supervisor, "x"));
        Assert.True(only.IsComplete);
        Assert.Empty(only.Results);
    }

    [Fact]
    public async Task Recall_rebuilds_remembered_results_in_the_order_asked()
    {
        var apps = new RecallingProvider(id => id == "gone" ? null : Fakes.Result(id));
        var supervisor = new Supervisor([Register("apps", apps)], _log, Options());

        var outcome = await supervisor.RecallAsync([new("apps", "b"), new("apps", "gone"), new("apps", "a")], Token);

        Assert.Equal(["b", "a"], outcome.Found.Select(r => r.Result.Id));
        Assert.All(outcome.Found, r => Assert.Equal("apps", r.ProviderId));
        Assert.Equal([new UsageKey("apps", "gone")], outcome.Gone);
    }

    [Fact]
    public async Task Recall_asks_nothing_and_forgets_nothing_before_a_provider_is_ready()
    {
        var apps = new RecallingProvider(_ => null, canRecall: false);
        var supervisor = new Supervisor([Register("apps", apps)], _log, Options());

        var outcome = await supervisor.RecallAsync([new("apps", "a")], Token);

        Assert.Empty(outcome.Found);
        Assert.Empty(outcome.Gone);
        Assert.Empty(apps.Asked);
    }

    [Fact]
    public async Task Recall_keeps_the_history_of_a_provider_that_is_not_here()
    {
        var supervisor = new Supervisor([Register("plain", ScriptedProvider.Returning())], _log, Options());

        var outcome = await supervisor.RecallAsync([new("plain", "a"), new("uninstalled.plugin", "b")], Token);

        Assert.Empty(outcome.Found);
        Assert.Empty(outcome.Gone);
    }

    [Fact]
    public async Task A_recall_that_throws_is_a_fault_and_forgets_nothing()
    {
        var apps = new RecallingProvider(_ => throw new InvalidOperationException("bug"));
        var supervisor = new Supervisor([Register("apps", apps)], _log, Options());

        var outcome = await supervisor.RecallAsync([new("apps", "a")], Token);

        Assert.Empty(outcome.Found);
        Assert.Empty(outcome.Gone);
        Assert.Contains(_log.Lines, l => l.Contains("apps faulted", StringComparison.Ordinal));
    }

    private static async IAsyncEnumerable<Result> Delayed(int milliseconds, [EnumeratorCancellation] CancellationToken ct, params Result[] results)
    {
        await Task.Delay(milliseconds, ct);
        foreach (var result in results)
        {
            yield return result;
        }
    }

    private static async IAsyncEnumerable<Result> Throwing()
    {
        await Task.Yield();
        throw new InvalidOperationException("provider bug");
#pragma warning disable CS0162 // Unreachable: the yield makes this an iterator.
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>Never finishes and never looks at its token: the provider a supervisor exists for.</summary>
    private static async IAsyncEnumerable<Result> Ignoring()
    {
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
        yield break;
    }

    private static async IAsyncEnumerable<Result> Endless([EnumeratorCancellation] CancellationToken ct, bool delay = true)
    {
        for (var i = 0; ; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (delay)
            {
                await Task.Delay(10, ct);
            }

            yield return Fakes.Result($"r{i}");
        }
    }
}
