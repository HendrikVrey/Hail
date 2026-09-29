using System.Runtime.CompilerServices;
using Hail.Core.Ports;
using Hail.Sdk;

namespace Hail.Providers.Apps;

/// <summary>
/// Finds apps by name among everything the Start menu lists, and starts the one chosen
/// (Hail.md §7.1).
/// </summary>
/// <remarks>
/// Reads the catalog on every query rather than keeping a copy, so a refresh the host makes
/// in the background is seen by the next keystroke with nothing to invalidate. The whole
/// list is a few hundred names held in memory, which is why this provider declares no
/// debounce: matching it costs less than drawing a row.
/// </remarks>
public sealed class AppsProvider(IAppCatalog catalog) : IProvider
{
    public const string ProviderId = "hail.apps";

    private IPluginContext? _context;

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<Result> QueryAsync(Query query, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var context = _context
            ?? throw new InvalidOperationException($"{nameof(AppsProvider)} was queried before it was initialised.");

        // An empty box shows what the user picks most (M1, from history), never the whole
        // Start menu in alphabetical order.
        if (query.Search.Length == 0)
        {
            yield break;
        }

        foreach (var app in catalog.Apps)
        {
            ct.ThrowIfCancellationRequested();

            var match = context.Matcher.Match(query.Search, app.Name);
            if (match is null)
            {
                continue;
            }

            yield return ToResult(app, match, context.Launcher);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static Result ToResult(AppEntry app, MatchResult match, ILauncher launcher) =>
        new(
            Id: app.Id,
            Title: app.Name,
            Subtitle: null,
            Icon: IconSource.ForShellItem(app.ShellPath),
            Relevance: match.Score,
            Primary: new ResultAction(
                "Open",
                Gesture.Enter,
                async (_, ct) =>
                {
                    await launcher.LaunchAppAsync(app.Id, ct).ConfigureAwait(false);
                    return ActionOutcome.Hide;
                }),
            Secondary: [],
            Highlight: match.Spans);
}
