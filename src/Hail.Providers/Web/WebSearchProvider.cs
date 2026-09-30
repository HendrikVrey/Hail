using System.Runtime.CompilerServices;
using Hail.Core.Ports;
using Hail.Sdk;

namespace Hail.Providers.Web;

/// <summary>
/// Sends a search to the browser (Hail.md §7.4). A keyword picks the engine (<c>g cats</c>);
/// without one it offers a last row, <em>Search Google for "cats"</em>, which sits below
/// everything else and is what an otherwise empty box shows.
/// </summary>
/// <remarks>
/// Nothing leaves the machine until Enter is pressed on one of these rows (Hail.md §9): no
/// suggestions are fetched. Not an <see cref="IRecall"/>, because the id of a search is what
/// was searched for.
/// </remarks>
public sealed class WebSearchProvider(WebSearchOptions options) : IProvider
{
    public const string ProviderId = "hail.web";

    private const string GlobeGlyph = "\uE774";

    /// <summary>A last resort (<see cref="Result.LastResort"/>): shown once everything else has answered, below it.</summary>
    private const double FallbackRelevance = 0.01;

    private readonly List<Engine> _engines = [];
    private IPluginContext? _context;
    private Engine? _default;

    /// <summary>The keywords of the engines that passed their checks, for the host to route by.</summary>
    public IReadOnlyList<(string Keyword, string Name)> Keywords => [.. _engines.Select(e => (e.Keyword, e.Name))];

    /// <summary>
    /// Checks every engine now, rather than at initialisation, so the host routes only the
    /// keywords that will work. An engine that fails is left out and said why in the log.
    /// </summary>
    public IReadOnlyList<string> Load()
    {
        ArgumentNullException.ThrowIfNull(options);
        _engines.Clear();
        var problems = new List<string>();
        foreach (var setting in options.Engines ?? [])
        {
            if (setting is null || string.IsNullOrWhiteSpace(setting.Keyword) || string.IsNullOrWhiteSpace(setting.Name))
            {
                problems.Add("A web engine without a keyword or a name was left out.");
                continue;
            }

            var template = WebTemplate.TryCreate(setting.Template, out var problem);
            if (template is null)
            {
                problems.Add($"The web engine \"{setting.Name}\" was left out: {problem}.");
                continue;
            }

            if (_engines.Any(e => string.Equals(e.Keyword, setting.Keyword, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"The web engine \"{setting.Name}\" was left out: another engine already uses the keyword \"{setting.Keyword}\".");
                continue;
            }

            _engines.Add(new Engine(setting.Keyword.Trim(), setting.Name.Trim(), template));
        }

        _default = _engines.FirstOrDefault(e => string.Equals(e.Keyword, options.DefaultKeyword, StringComparison.OrdinalIgnoreCase))
            ?? _engines.FirstOrDefault();
        return problems;
    }

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        if (_engines.Count == 0)
        {
            Load();
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<Result> QueryAsync(Query query, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var context = _context
            ?? throw new InvalidOperationException($"{nameof(WebSearchProvider)} was queried before it was initialised.");
        ct.ThrowIfCancellationRequested();

        // A path is not a search: the files provider lists it, and a fallback row would only be
        // one more thing to read past.
        var engine = query.IsKeywordScoped
            ? _engines.FirstOrDefault(e => string.Equals(e.Keyword, query.Keyword, StringComparison.OrdinalIgnoreCase))
            : LooksLikePath(query.Search) ? null : _default;

        if (engine is not null)
        {
            if (query.Search.Length > 0)
            {
                yield return Search(engine, query.Search, query.IsKeywordScoped ? 1.0 : FallbackRelevance, context);
            }
            else if (query.IsKeywordScoped)
            {
                yield return Prompt(engine);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static bool LooksLikePath(string search) => Files.PathMode.IsPathLike(search);

    private static Result Search(Engine engine, string search, double relevance, IPluginContext context)
    {
        var uri = engine.Template.UriFor(search);
        return new Result(
            Id: $"search:{engine.Keyword}",
            Title: $"Search {engine.Name} for “{search}”",
            Subtitle: uri.Host,
            Icon: IconSource.ForGlyph(GlobeGlyph),
            Relevance: relevance,
            Primary: new ResultAction("Search", Gesture.Enter, async (_, ct) =>
            {
                await context.Launcher.OpenUriAsync(uri, ct).ConfigureAwait(false);
                return ActionOutcome.Hide;
            }),
            Secondary: [new ResultAction("Copy link", Gesture.CtrlC, async (_, ct) =>
            {
                await context.Clipboard.SetTextAsync(uri.AbsoluteUri, ct).ConfigureAwait(false);
                return ActionOutcome.Hide;
            })],
            Highlight: null);
    }

    private static Result Prompt(Engine engine) =>
        new(
            Id: $"prompt:{engine.Keyword}",
            Title: $"Type to search {engine.Name}",
            Subtitle: null,
            Icon: IconSource.ForGlyph(GlobeGlyph),
            Relevance: 1.0,
            Primary: new ResultAction("Search", Gesture.Enter, (_, _) => ValueTask.FromResult(ActionOutcome.KeepOpen)),
            Secondary: [],
            Highlight: null);

    private sealed record Engine(string Keyword, string Name, WebTemplate Template);
}
