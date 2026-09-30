using System.Runtime.CompilerServices;
using Hail.Core.Ports;
using Hail.Sdk;

namespace Hail.Providers.Files;

/// <summary>
/// Finds files and folders (Hail.md §7.2) two ways. Text that looks like a path
/// (<c>C:\Users\</c>, <c>~\Doc</c>) lists that folder directly, and Tab completes the
/// highlighted entry into the box; anything else asks the Windows Search index, which knows
/// the folders the user has told Windows to index and nothing more.
/// </summary>
/// <remarks>
/// <para>
/// The index is asked only once typing pauses (the host applies the debounce this provider is
/// registered with) and only for two characters or more; its answers join the box below
/// whatever is highlighted. A network path (<c>\\server\</c>) is not listed as it is typed,
/// because each keystroke would reach a server that may not be one's own.
/// </para>
/// <para>
/// An <see cref="IRecall"/>: a file picked is remembered by its path, and an empty box offers
/// it again while it still exists.
/// </para>
/// </remarks>
public sealed class FilesProvider(IFileIndex index, ILocalFiles files) : IProvider, IRecall
{
    public const string ProviderId = "hail.files";

    /// <summary>Rows asked of the index; the box shows eight, and the rest rank against apps.</summary>
    public const int IndexRows = 40;

    /// <summary>Entries read from a folder in path mode before matching.</summary>
    public const int FolderEntries = 2000;

    private const int MinimumSearch = 2;
    private const string WarningGlyph = "\uE7BA";

    /// <summary>Below an app of the same name, so "notepad" is the program before notepad.txt.</summary>
    private const double IndexWeight = 0.8;

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

        var search = query.Search;
        if (PathMode.TryParse(search, files.HomeFolder, out var folder, out var partial))
        {
            foreach (var result in ListFolder(folder, partial, context, ct))
            {
                yield return result;
            }

            yield break;
        }

        // A path that cannot be listed (a network path, one that climbs with "..") is not a
        // name to look up either.
        if (search.Length < MinimumSearch || PathMode.IsPathLike(search))
        {
            yield break;
        }

        var answer = await index.SearchAsync(search, IndexRows, ct).ConfigureAwait(false);
        switch (answer)
        {
            case FileIndexAnswer.Unavailable unavailable:
                yield return Notice(unavailable.Reason);
                break;

            case FileIndexAnswer.Found found:
                foreach (var item in found.Items.Where(i => !IsStartMenuShortcut(i.Path)))
                {
                    ct.ThrowIfCancellationRequested();
                    var match = context.Matcher.Match(search, item.Name);
                    if (match is not null)
                    {
                        yield return ToResult(item, match.Score * IndexWeight, match.Spans, context);
                    }
                }

                break;
        }
    }

    public bool CanRecall => true;

    /// <summary>
    /// A shortcut in either Start menu folder is an app, and the apps provider already lists it
    /// by its proper name; as a file it would be the same thing twice, as <em>Notepad++.lnk</em>.
    /// </summary>
    public static bool IsStartMenuShortcut(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Contains(@"\Microsoft\Windows\Start Menu\", StringComparison.OrdinalIgnoreCase);
    }

    public ValueTask<Result?> RecallAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        var item = files.Describe(id);
        return ValueTask.FromResult(item is null ? null : ToResult(item, relevance: 1.0, highlight: null, Context()));
    }

    private IEnumerable<Result> ListFolder(string folder, string partial, IPluginContext context, CancellationToken ct)
    {
        var listing = files.List(folder, FolderEntries, ct);
        foreach (var item in listing.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (partial.Length == 0)
            {
                // Folders first, then files, each alphabetically (the ranker breaks the tie by title).
                yield return Completing(ToResult(item, item.IsFolder ? 0.91 : 0.9, highlight: null, context), item);
                continue;
            }

            var match = context.Matcher.Match(partial, item.Name);
            if (match is not null)
            {
                yield return Completing(ToResult(item, 0.5 + (0.5 * match.Score), match.Spans, context), item);
            }
        }
    }

    private static Result Completing(Result result, LocalItem item) =>
        result with { Completion = item.IsFolder ? item.Path.TrimEnd('\\') + "\\" : item.Path };

    private static Result ToResult(LocalItem item, double relevance, MatchSpans? highlight, IPluginContext context)
    {
        var path = item.Path;
        var secondary = new List<ResultAction>
        {
            new("Copy path", Gesture.CtrlC, async (_, ct) =>
            {
                await context.Clipboard.SetTextAsync(path, ct).ConfigureAwait(false);
                return ActionOutcome.Hide;
            }),
        };

        if (!item.IsFolder)
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
            Title: item.Name,
            Subtitle: Path.GetDirectoryName(path) ?? path,
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

    private static Result Notice(string reason) =>
        new(
            Id: "notice",
            Title: reason,
            Subtitle: "Apps, sums and web searches still work.",
            Icon: IconSource.ForGlyph(WarningGlyph),
            Relevance: 0.005,
            Primary: new ResultAction("Nothing to open", Gesture.Enter, (_, _) => ValueTask.FromResult(ActionOutcome.KeepOpen)),
            Secondary: [],
            Highlight: null);

    private IPluginContext Context() =>
        _context ?? throw new InvalidOperationException($"{nameof(FilesProvider)} was used before it was initialised.");
}

/// <summary>Reads text that looks like a path into the folder to list and the name being typed.</summary>
public static class PathMode
{
    /// <summary>
    /// True for <c>C:\...</c>, <c>C:/...</c> and <c>~\...</c>: the folder is everything up to
    /// the last separator, the partial name is what follows it.
    /// </summary>
    public static bool TryParse(string text, string home, out string folder, out string partial)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(home);
        folder = string.Empty;
        partial = string.Empty;

        var path = text.Trim().Replace('/', '\\');
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        {
            // A drive path, as typed.
        }
        else if (path == "~" || path.StartsWith(@"~\", StringComparison.Ordinal))
        {
            path = home.TrimEnd('\\') + @"\" + path[1..].TrimStart('\\');
        }
        else
        {
            return false;
        }

        var cut = path.LastIndexOf('\\');
        folder = path[..(cut + 1)];
        partial = path[(cut + 1)..];

        // "C:\a\..\b" is not listed: a folder is what was typed, never somewhere else.
        return !folder.Split('\\').Any(segment => segment is "." or "..");
    }

    /// <summary>
    /// Whether the text is written as a path at all: a drive (<c>C:\</c>), the home folder
    /// (<c>~\</c>) or a network path (<c>\\server\</c>), listable or not.
    /// </summary>
    public static bool IsPathLike(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var path = text.Trim().Replace('/', '\\');
        return (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
            || path == "~"
            || path.StartsWith(@"~\", StringComparison.Ordinal)
            || path.StartsWith(@"\\", StringComparison.Ordinal);
    }
}
