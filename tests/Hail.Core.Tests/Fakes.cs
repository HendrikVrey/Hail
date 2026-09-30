using System.Runtime.CompilerServices;
using Hail.Core.Hosting;
using Hail.Core.Matching;
using Hail.Core.Ports;
using Hail.Core.Ranking;
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

    public static ProviderResult At(Result result, int order = 0, string provider = "p") => new(result, order, provider);

    public static ProviderResult Row(string title, double relevance = 0.5, int order = 0, string provider = "p") =>
        At(Result(title, relevance), order, provider);

    public static ProviderRegistration Register(string id, IProvider provider, IHostLog log) =>
        new(id, id, provider, new PluginContext(id, new NullLauncher(), new FuzzyMatcher(), new NullClipboard(), log));
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

    public string[] Errors()
    {
        lock (Lines)
        {
            return [.. Lines.Where(l => l.StartsWith("ERROR", StringComparison.Ordinal))];
        }
    }
}

internal sealed class NullLauncher : ILauncher
{
    public ValueTask LaunchAppAsync(string appId, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask LaunchAppAsAdministratorAsync(string appId, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask OpenUriAsync(Uri uri, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask OpenPathAsync(string path, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask ShowInFolderAsync(string path, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class NullClipboard : IClipboard
{
    public ValueTask SetTextAsync(string text, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask SetFileAsync(string path, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A provider whose behaviour each test sets.</summary>
internal class ScriptedProvider(Func<Query, CancellationToken, IAsyncEnumerable<Result>> query) : IProvider
{
    private int _initialisations;
    private int _queries;

    public int Initialisations => Volatile.Read(ref _initialisations);

    public int Queries => Volatile.Read(ref _queries);

    public Func<ValueTask>? OnInitialize { get; init; }

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref _initialisations);
        if (OnInitialize is not null)
        {
            await OnInitialize().ConfigureAwait(false);
        }
    }

    public IAsyncEnumerable<Result> QueryAsync(Query q, CancellationToken ct)
    {
        Interlocked.Increment(ref _queries);
        return query(q, ct);
    }

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

/// <summary>A scripted provider that can also rebuild results by id.</summary>
internal sealed class RecallingProvider(Func<string, Result?> recall, bool canRecall = true)
    : ScriptedProvider((_, ct) => Yield([], ct)), IRecall
{
    public bool CanRecall { get; set; } = canRecall;

    public List<string> Asked { get; } = [];

    public ValueTask<Result?> RecallAsync(string id, CancellationToken ct)
    {
        lock (Asked)
        {
            Asked.Add(id);
        }

        return ValueTask.FromResult(recall(id));
    }
}
