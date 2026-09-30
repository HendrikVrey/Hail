using System.Runtime.CompilerServices;
using Hail.Sdk;
using Hail.TestShared;

namespace Hail.TestPlugin.Alpha;

/// <summary>Answers every query with one row naming the shared library's version and, if declared, a setting.</summary>
public sealed class AlphaProvider : IProvider, IRecall, IDisposable
{
    private IPluginContext? _context;

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<Result> QueryAsync(Query query, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return Row($"alpha {Library.Version} {query.Search}");

        if (Settings.Has(_context!, "greeting"))
        {
            yield return Row($"greeting {_context!.Settings.GetText("greeting")}");
            yield return Row($"secret {_context.Settings.GetSecret("token") ?? "(none)"}");
        }

        await Task.CompletedTask;
    }

    public ValueTask<Recollection> RecallAsync(string id, CancellationToken ct) =>
        ValueTask.FromResult(Recollection.Of(Row(id)));

    public void Dispose() => Disposals.Count++;

    internal static Result Row(string title) =>
        new(title, title, null, IconSource.None, 0.9, new ResultAction("Open", Gesture.Enter, (_, _) => ValueTask.FromResult(ActionOutcome.Hide)), [], null);
}

/// <summary>Counts disposals where the host's test can read it only through a result, never a type.</summary>
public static class Disposals
{
    public static int Count { get; set; }
}

internal static class Settings
{
    public static bool Has(IPluginContext context, string key)
    {
        try
        {
            _ = context.Settings.GetText(key);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public sealed class ThrowingProvider : IProvider
{
    public ThrowingProvider() => throw new InvalidOperationException("secret words the host must not log");

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct) => ValueTask.CompletedTask;

    public IAsyncEnumerable<Result> QueryAsync(Query query, CancellationToken ct) => throw new NotSupportedException();
}

public sealed class NotAProvider
{
}

/// <summary>Starts a thread that never ends and is never told to: the plugin cannot be unloaded.</summary>
public sealed class ThreadLeavingProvider : IProvider
{
    private static readonly ManualResetEventSlim Never = new();

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        new Thread(() => Never.Wait()) { IsBackground = true }.Start();
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<Result> QueryAsync(Query query, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return AlphaProvider.Row("leaving");
        await Task.CompletedTask;
    }
}
