using System.Globalization;
using System.Runtime.CompilerServices;
using Hail.Sdk;

namespace Hail.Plugin.Everything;

/// <summary>
/// Every file and folder on every drive, from Everything by voidtools (Hail.md §7.2): after
/// <c>e</c>, what is typed goes to Everything in its own search syntax, and what it finds is
/// ranked by Hail's matcher like any other file. <c>e</c> with nothing after it offers the
/// files picked from here most.
/// </summary>
/// <remarks>
/// Written against <c>Hail.Sdk</c> alone, as a stranger's plugin would be: everything it opens,
/// shows or copies goes through the context, where Hail's rules apply.
/// </remarks>
public sealed class EverythingProvider : IProvider, IRecall, IDisposable
{
    /// <summary>The rows offered for <c>e</c> alone.</summary>
    private const int Recent = 8;

    private const int MinimumSearch = 2;

    /// <summary>A match on the name counts as a Windows Search one does: below an app of the same name.</summary>
    private const double Weight = 0.8;

    /// <summary>What Everything found by its own rules (a path, a wildcard) where the name does not match.</summary>
    private const double Unmatched = 0.2;

    private const string WarningGlyph = "\uE7BA";

    private readonly IEverything _everything;
    private IPluginContext? _context;

    /// <summary>Made by Hail when it loads the plugin.</summary>
    public EverythingProvider()
        : this(new EverythingClient())
    {
    }

    internal EverythingProvider(IEverything everything) => _everything = everything;

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
        var search = query.Search;

        if (search.Length == 0)
        {
            foreach (var path in context.History.MostPicked(Recent))
            {
                if (Describe(path) is { } item)
                {
                    yield return ToResult(item, 0.9, highlight: null, context);
                }
            }

            yield break;
        }

        if (search.Length < MinimumSearch)
        {
            yield break;
        }

        IReadOnlyList<EverythingItem> items = [];
        string? problem = null;
        try
        {
            items = await _everything.SearchAsync(search, Flags(context), MaxResults(context), ct).ConfigureAwait(false);
        }
        catch (EverythingUnavailableException unavailable)
        {
            problem = unavailable.Message;
        }
        catch (InvalidDataException)
        {
            problem = "Everything's answer could not be read.";
        }

        if (problem is not null)
        {
            context.Log.LogInfo($"No search: {problem}");
            yield return Notice(problem);
            yield break;
        }

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            // Hail opens nothing on another machine; a row that cannot be opened is not offered.
            if (item.FullPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                continue;
            }

            var match = context.Matcher.Match(search, item.Name);
            yield return ToResult(item, match is null ? Unmatched : match.Score * Weight, match?.Spans, context);
        }
    }

    /// <remarks>
    /// A file on a drive that is not there now (a USB stick unplugged) cannot be told gone:
    /// history keeps it for when the drive is back.
    /// </remarks>
    public ValueTask<Recollection> RecallAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        var answer = Describe(id) is { } item ? Recollection.Of(ToResult(item, 1.0, highlight: null, Context()))
            : !id.StartsWith(@"\\", StringComparison.Ordinal) && Path.GetPathRoot(id) is { Length: > 0 } root && Directory.Exists(root) ? Recollection.Gone
            : Recollection.Unknown;
        return ValueTask.FromResult(answer);
    }

    /// <summary>Closes the window Everything answers to, so Hail can unload the plugin.</summary>
    public void Dispose() => _everything.Dispose();

    private static uint Flags(IPluginContext context) =>
        context.Settings.GetToggle("matchPath") ? EverythingIpc.MatchPath : 0;

    private static int MaxResults(IPluginContext context) =>
        int.TryParse(context.Settings.GetChoice("maxResults"), NumberStyles.None, CultureInfo.InvariantCulture, out var max) ? max : 50;

    /// <summary>The item at <paramref name="path"/> as it is on disk now, or null when it is not there.</summary>
    private static EverythingItem? Describe(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        var name = Path.GetFileName(path);
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        return File.Exists(path) ? new EverythingItem(name, folder, IsFolder: false, IsDrive: false)
            : Directory.Exists(path) ? new EverythingItem(name, folder, IsFolder: true, IsDrive: false)
            : null;
    }

    private static Result ToResult(EverythingItem item, double relevance, MatchSpans? highlight, IPluginContext context)
    {
        var path = item.FullPath;
        var secondary = new List<ResultAction>
        {
            new("Copy path", Gesture.CtrlC, async (_, ct) =>
            {
                await context.Clipboard.SetTextAsync(path, ct).ConfigureAwait(false);
                return ActionOutcome.Hide;
            }),
        };

        if (!item.IsFolder && !item.IsDrive)
        {
            secondary.Insert(0, new ResultAction("Show in folder", Gesture.CtrlEnter, async (_, ct) =>
            {
                await context.Launcher.ShowInFolderAsync(path, ct).ConfigureAwait(false);
                return ActionOutcome.Hide;
            }));
            secondary.Add(new ResultAction("Copy file", Gesture.CtrlShiftC, async (_, ct) =>
            {
                await context.Clipboard.SetFileAsync(path, ct).ConfigureAwait(false);
                return ActionOutcome.Hide;
            }));
        }

        return new Result(
            Id: path,
            Title: item.Name.Length > 0 ? item.Name : path,
            Subtitle: item.Folder.Length > 0 ? item.Folder : null,
            Icon: IconSource.ForShellItem(path),
            Relevance: relevance,
            Primary: new ResultAction("Open", Gesture.Enter, async (_, ct) =>
            {
                await context.Launcher.OpenPathAsync(path, ct).ConfigureAwait(false);
                return ActionOutcome.Hide;
            }),
            Secondary: secondary,
            Highlight: highlight);
    }

    private static Result Notice(string problem) =>
        new(
            Id: "notice",
            Title: problem,
            Subtitle: "Start Everything (voidtools) to search every file on every drive.",
            Icon: IconSource.ForGlyph(WarningGlyph),
            Relevance: 0.5,
            Primary: new ResultAction("Nothing to open", Gesture.Enter, (_, _) => ValueTask.FromResult(ActionOutcome.KeepOpen)),
            Secondary: [],
            Highlight: null);

    private IPluginContext Context() =>
        _context ?? throw new InvalidOperationException($"{nameof(EverythingProvider)} was used before it was initialised.");
}
