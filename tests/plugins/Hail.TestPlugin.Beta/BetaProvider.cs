using System.Runtime.CompilerServices;
using Hail.Sdk;
using Hail.TestShared;

namespace Hail.TestPlugin.Beta;

public sealed class BetaProvider : IProvider
{
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<Result> QueryAsync(Query query, [EnumeratorCancellation] CancellationToken ct)
    {
        var title = $"beta {Library.Version}";
        yield return new Result(title, title, null, IconSource.None, 0.9, new ResultAction("Open", Gesture.Enter, (_, _) => ValueTask.FromResult(ActionOutcome.Hide)), [], null);
        await Task.CompletedTask;
    }
}
