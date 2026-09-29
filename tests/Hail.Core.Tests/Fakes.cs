using System.Runtime.CompilerServices;
using Hail.Core.Ports;
using Hail.Sdk;

namespace Hail.Core.Tests;

internal static class Fakes
{
    public static Result Result(string title, double relevance = 0.5, string? id = null) =>
        new(
            id ?? title,
            title,
            Subtitle: null,
            IconSource.None,
            relevance,
            new ResultAction("Open", Gesture.Enter, (_, _) => ValueTask.FromResult(ActionOutcome.Hide)),
            Secondary: [],
            Highlight: null);
}

internal sealed class RecordingLog : IHostLog
{
    public List<string> Lines { get; } = [];

    public void LogInfo(string message)
    {
        lock (Lines)
        {
            Lines.Add("INFO " + message);
        }
    }

    public void LogError(string message, Exception? exception = null)
    {
        lock (Lines)
        {
            Lines.Add("ERROR " + message);
        }
    }
}

/// <summary>A provider whose behaviour each test sets.</summary>
internal sealed class ScriptedProvider(Func<Query, CancellationToken, IAsyncEnumerable<Result>> query) : IProvider
{
    public int Initialisations { get; private set; }

    public Func<ValueTask>? OnInitialize { get; init; }

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        Initialisations++;
        if (OnInitialize is not null)
        {
            await OnInitialize().ConfigureAwait(false);
        }
    }

    public IAsyncEnumerable<Result> QueryAsync(Query q, CancellationToken ct) => query(q, ct);

    public static ScriptedProvider Returning(params Result[] results) => new((_, ct) => Yield(results, ct));

    public static async IAsyncEnumerable<Result> Yield(IEnumerable<Result> results, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var result in results)
        {
            ct.ThrowIfCancellationRequested();
            yield return result;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
