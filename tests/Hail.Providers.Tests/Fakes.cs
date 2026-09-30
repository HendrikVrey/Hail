using Hail.Core.Hosting;
using Hail.Core.Matching;
using Hail.Core.Ports;
using Hail.Sdk;

namespace Hail.Providers.Tests;

/// <summary>What a provider asked the host to do, in order.</summary>
internal sealed class FakeLauncher : ILauncher
{
    public List<string> Calls { get; } = [];

    public ValueTask LaunchAppAsync(string appId, CancellationToken ct) => Record($"launch {appId}");

    public ValueTask LaunchAppAsAdministratorAsync(string appId, CancellationToken ct) => Record($"elevate {appId}");

    public ValueTask OpenUriAsync(Uri uri, CancellationToken ct) => Record($"uri {uri.AbsoluteUri}");

    public ValueTask OpenPathAsync(string path, CancellationToken ct) => Record($"open {path}");

    public ValueTask ShowInFolderAsync(string path, CancellationToken ct) => Record($"show {path}");

    private ValueTask Record(string call)
    {
        Calls.Add(call);
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeClipboard : IClipboard
{
    public List<string> Calls { get; } = [];

    public ValueTask SetTextAsync(string text, CancellationToken ct)
    {
        Calls.Add($"text {text}");
        return ValueTask.CompletedTask;
    }

    public ValueTask SetFileAsync(string path, CancellationToken ct)
    {
        Calls.Add($"file {path}");
        return ValueTask.CompletedTask;
    }
}

internal sealed class NullLog : IHostLog
{
    public void LogInfo(string message)
    {
    }

    public void LogError(string message, Exception? exception = null)
    {
    }
}

internal static class Harness
{
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public static PluginContext Context(FakeLauncher launcher, FakeClipboard clipboard) =>
        new("test", launcher, new FuzzyMatcher(), clipboard, new NullLog());

    public static async Task<List<Result>> CollectAsync(IProvider provider, Query query)
    {
        var results = new List<Result>();
        await foreach (var result in provider.QueryAsync(query, Token))
        {
            results.Add(result);
        }

        return results;
    }

    public static Query Scoped(string keyword, string search) => new($"{keyword} {search}", search, keyword, IsKeywordScoped: true);

    public static ValueTask<ActionOutcome> Run(ResultAction action, Query? query = null) =>
        action.Execute(new ActionContext(query ?? Query.Global(string.Empty)), Token);

    public static ResultAction On(Result result, Gesture gesture) =>
        gesture == Gesture.Enter ? result.Primary : result.Secondary.Single(a => a.Gesture == gesture);
}
