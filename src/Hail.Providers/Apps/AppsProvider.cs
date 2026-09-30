using System.Runtime.CompilerServices;
using Hail.Core.Ports;
using Hail.Sdk;

namespace Hail.Providers.Apps;

/// <summary>
/// Finds apps by name among everything the Start menu lists, and starts the one chosen
/// (Hail.md §7.1).
/// </summary>
/// <remarks>
/// <para>
/// Reads the catalog on every query rather than keeping a copy, so a refresh the host makes
/// in the background is seen by the next keystroke with nothing to invalidate. The whole
/// list is a few hundred names held in memory, which is why this provider declares no
/// debounce: matching it costs less than drawing a row.
/// </para>
/// <para>
/// An <see cref="IRecall"/>: the apps picked most fill the empty box.
/// </para>
/// </remarks>
public sealed class AppsProvider(IAppCatalog catalog) : IProvider, IRecall
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
        var context = Context();

        // An empty box shows what the user picks most, from history, never the whole Start
        // menu in alphabetical order.
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

            yield return ToResult(app, match.Score, match.Spans, context.Launcher);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// An empty list means the Start menu has not been read yet, not that every app is gone,
    /// so nothing is recalled, and nothing forgotten, until it has been.
    /// </summary>
    public bool CanRecall => catalog.Apps.Count > 0;

    public ValueTask<Result?> RecallAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        var context = Context();

        var app = catalog.Apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        return ValueTask.FromResult(app is null ? null : ToResult(app, 1.0, highlight: null, context.Launcher));
    }

    private IPluginContext Context() =>
        _context ?? throw new InvalidOperationException($"{nameof(AppsProvider)} was used before it was initialised.");

    private static Result ToResult(AppEntry app, double relevance, MatchSpans? highlight, ILauncher launcher)
    {
        // A packaged app is started by its package's own rules; Windows offers no elevated
        // start for most of them, so neither does Hail.
        IReadOnlyList<ResultAction> secondary = app.IsPackaged
            ? []
            : [new ResultAction("Run as administrator", Gesture.CtrlShiftEnter, async (_, ct) =>
            {
                await launcher.LaunchAppAsAdministratorAsync(app.Id, ct).ConfigureAwait(false);
                return ActionOutcome.Hide;
            })];

        return new Result(
            Id: app.Id,
            Title: app.Name,
            Subtitle: null,
            Icon: IconSource.ForShellItem(app.ShellPath),
            Relevance: relevance,
            Primary: new ResultAction("Open", Gesture.Enter, async (_, ct) =>
            {
                await launcher.LaunchAppAsync(app.Id, ct).ConfigureAwait(false);
                return ActionOutcome.Hide;
            }),
            Secondary: secondary,
            Highlight: highlight);
    }
}
