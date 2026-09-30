using System.Runtime.CompilerServices;
using Hail.Sdk;

namespace HailPlugin1;

/// <summary>
/// Hail's box asks this for results as the user types after its keyword. Everything it opens,
/// copies or shows goes through the context Hail hands it, where Hail's rules apply.
/// </summary>
public sealed class Provider : IProvider
{
    private IPluginContext? _context;

    /// <summary>Called once, before the first search that reaches the plugin. Keep it quick.</summary>
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Called on every keystroke that reaches the plugin, on a background thread. The token is
    /// cancelled by the next keystroke: honour it.
    /// </summary>
    public async IAsyncEnumerable<Result> QueryAsync(Query query, [EnumeratorCancellation] CancellationToken ct)
    {
        var context = _context ?? throw new InvalidOperationException("Hail initialises a provider before it asks it anything.");
        if (query.Search.Length == 0)
        {
            yield break;
        }

        var text = $"{context.Settings.GetText("greeting")}, {query.Search}";
        yield return new Result(
            Id: "greeting",
            Title: text,
            Subtitle: "Enter copies it",
            Icon: IconSource.ForGlyph("\uE8BD"),
            Relevance: 0.9,
            Primary: new ResultAction("Copy", Gesture.Enter, async (_, token) =>
            {
                await context.Clipboard.SetTextAsync(text, token).ConfigureAwait(false);
                return ActionOutcome.Hide;
            }),
            Secondary: [],
            Highlight: null);

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
