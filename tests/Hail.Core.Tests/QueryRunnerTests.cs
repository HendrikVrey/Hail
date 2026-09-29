using System.Runtime.CompilerServices;
using Hail.Core.Hosting;
using Hail.Core.Matching;
using Hail.Sdk;

namespace Hail.Core.Tests;

public sealed class QueryRunnerTests
{
    private readonly RecordingLog _log = new();

    private ProviderRegistration Register(string id, IProvider provider) =>
        new(id, provider, new PluginContext(id, new NoLauncher(), new FuzzyMatcher(), _log));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Results_carry_their_providers_order()
    {
        var runner = new QueryRunner(
            [Register("first", ScriptedProvider.Returning(Fakes.Result("a"))), Register("second", ScriptedProvider.Returning(Fakes.Result("b")))],
            _log);

        var results = await runner.RunAsync(Query.Global("x"), Token);

        Assert.Equal([("a", 0), ("b", 1)], results.Select(r => (r.Result.Id, r.ProviderOrder)));
    }

    [Fact]
    public async Task A_provider_is_initialised_once_across_queries()
    {
        var provider = ScriptedProvider.Returning(Fakes.Result("a"));
        var runner = new QueryRunner([Register("p", provider)], _log);

        await runner.RunAsync(Query.Global("x"), Token);
        await runner.RunAsync(Query.Global("y"), Token);

        Assert.Equal(1, provider.Initialisations);
    }

    [Fact]
    public async Task A_provider_that_throws_costs_itself_and_not_the_others()
    {
        var broken = new ScriptedProvider((_, _) => Throwing());
        var runner = new QueryRunner(
            [Register("broken", broken), Register("fine", ScriptedProvider.Returning(Fakes.Result("ok")))],
            _log);

        var results = await runner.RunAsync(Query.Global("secret words"), Token);

        Assert.Equal(["ok"], results.Select(r => r.Result.Id));
        var line = Assert.Single(_log.Lines, l => l.Contains("broken", StringComparison.Ordinal));
        Assert.Contains("12 characters", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_that_fails_to_initialise_is_left_out_and_logged_once()
    {
        var provider = new ScriptedProvider((_, ct) => ScriptedProvider.Yield([Fakes.Result("never")], ct))
        {
            OnInitialize = () => throw new InvalidOperationException("no"),
        };
        var runner = new QueryRunner([Register("p", provider)], _log);

        Assert.Empty(await runner.RunAsync(Query.Global("a"), Token));
        Assert.Empty(await runner.RunAsync(Query.Global("b"), Token));

        Assert.Single(_log.Lines, l => l.Contains("failed to initialise", StringComparison.Ordinal));
        Assert.Equal(1, provider.Initialisations);
    }

    [Fact]
    public async Task Cancellation_reaches_the_caller()
    {
        var runner = new QueryRunner([Register("slow", new ScriptedProvider((_, ct) => Endless(ct)))], _log);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(Query.Global("x"), cancel.Token));
        Assert.DoesNotContain(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancelled_query_does_not_leave_the_provider_half_initialised()
    {
        var gate = new TaskCompletionSource();
        var provider = new ScriptedProvider((_, ct) => ScriptedProvider.Yield([Fakes.Result("a")], ct))
        {
            OnInitialize = () => new ValueTask(gate.Task),
        };
        var runner = new QueryRunner([Register("p", provider)], _log);

        using var cancel = new CancellationTokenSource();
        var first = runner.RunAsync(Query.Global("x"), cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        gate.SetResult();
        var results = await runner.RunAsync(Query.Global("y"), Token);

        Assert.Single(results);
        Assert.Equal(1, provider.Initialisations);
    }

    [Fact]
    public async Task A_provider_streaming_without_end_is_cut_off_at_the_cap()
    {
        var runner = new QueryRunner([Register("flood", new ScriptedProvider((_, ct) => Endless(ct, delay: false)))], _log);

        var results = await runner.RunAsync(Query.Global("x"), Token);

        Assert.Equal(QueryRunner.MaxResultsPerProvider, results.Count);
    }

    [Fact]
    public async Task Malformed_results_are_dropped()
    {
        var bad = Fakes.Result("x") with { Title = " " };
        var noId = Fakes.Result("y") with { Id = "" };
        var runner = new QueryRunner([Register("p", ScriptedProvider.Returning(bad, noId, Fakes.Result("good")))], _log);

        var results = await runner.RunAsync(Query.Global("x"), Token);

        Assert.Equal(["good"], results.Select(r => r.Result.Id));
    }

    private static async IAsyncEnumerable<Result> Throwing()
    {
        await Task.Yield();
        throw new InvalidOperationException("provider bug");
#pragma warning disable CS0162 // Unreachable: the yield makes this an iterator.
        yield break;
#pragma warning restore CS0162
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

    private sealed class NoLauncher : ILauncher
    {
        public ValueTask LaunchAppAsync(string appId, CancellationToken ct) => throw new NotSupportedException();
    }
}
