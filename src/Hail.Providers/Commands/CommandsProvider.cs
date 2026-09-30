using System.Runtime.CompilerServices;
using Hail.Core.Ports;
using Hail.Sdk;

namespace Hail.Providers.Commands;

/// <summary>
/// Hail's own commands and a few of Windows' (Hail.md §7.5). Lock runs at once, since nothing
/// is lost by it; Sleep, Sign out, Restart and Shut down ask first, in the box, because each
/// can cost unsaved work. Emptying the recycle bin is deliberately absent: it is a permanent
/// delete.
/// </summary>
public sealed class CommandsProvider(ISessionControl session, IHostCommands host) : IProvider, IRecall
{
    public const string ProviderId = "hail.commands";

    /// <summary>
    /// Below an app of the same strength of match, so "s" is Settings the app before Sleep;
    /// history lifts a command someone uses.
    /// </summary>
    private const double Weight = 0.85;

    private const int MinimumSearch = 2;

    private IPluginContext? _context;
    private IReadOnlyList<Command> _commands = [];

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _commands = Build(context);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<Result> QueryAsync(Query query, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var context = _context
            ?? throw new InvalidOperationException($"{nameof(CommandsProvider)} was queried before it was initialised.");

        if (query.Search.Length < MinimumSearch)
        {
            yield break;
        }

        foreach (var command in _commands)
        {
            ct.ThrowIfCancellationRequested();

            var onTitle = context.Matcher.Match(query.Search, command.Title);
            var onAlias = command.Aliases
                .Select(alias => context.Matcher.Match(query.Search, alias))
                .Where(m => m is not null)
                .MaxBy(m => m!.Score);

            if (onTitle is null && onAlias is null)
            {
                continue;
            }

            // An alias ("reboot") finds the command but bolds nothing in a title it is not in.
            var score = Math.Max(onTitle?.Score ?? 0, onAlias?.Score ?? 0);
            yield return command.ToResult(score * Weight, onTitle?.Spans);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask<Recollection> RecallAsync(string id, CancellationToken ct) =>
        ValueTask.FromResult(_commands.FirstOrDefault(c => c.Id == id) is { } command
            ? Recollection.Of(command.ToResult(1.0, highlight: null))
            : Recollection.Gone);

    private IReadOnlyList<Command> Build(IPluginContext context) =>
    [
        new("lock", "Lock", "Lock this PC; everything stays open", "\uE72E", ["lock screen", "lock computer"],
            Run(session.Lock), Question: null),
        new("sleep", "Sleep", "Put this PC to sleep", "\uE708", ["suspend"],
            Run(session.Sleep), "Put this PC to sleep?"),
        new("signout", "Sign out", "Sign out of Windows; open apps close", "\uF3B1", ["log off", "log out", "logout", "sign off"],
            Run(session.SignOut), "Sign out of Windows? Apps with unsaved work may lose it."),
        new("restart", "Restart", "Restart Windows; open apps close", "\uE777", ["reboot"],
            Run(session.Restart), "Restart Windows? Apps with unsaved work may lose it."),
        new("shutdown", "Shut down", "Turn this PC off; open apps close", "\uE7E8", ["shutdown", "power off", "turn off"],
            Run(session.ShutDown), "Shut down this PC? Apps with unsaved work may lose it."),
        new("hail.settings", "Hail settings", "Open Hail's settings file; restart Hail to apply changes", "\uE713", ["hail options", "preferences"],
            async ct =>
            {
                await context.Launcher.OpenPathAsync(host.SettingsPath, ct).ConfigureAwait(false);
            },
            Question: null),
        new("hail.plugins", "Open plugins folder", "Where Hail's plugins are installed, one folder each", "\uE838", ["plugins folder", "hail plugins"],
            async ct =>
            {
                await context.Launcher.OpenPathAsync(host.PluginsFolder, ct).ConfigureAwait(false);
            },
            Question: null),
        new("hail.reload", "Reload plugins", "Read the plugins folder again and start its plugins afresh", "\uE72C", ["refresh plugins"],
            _ =>
            {
                host.ReloadPlugins();
                return ValueTask.CompletedTask;
            },
            Question: null),
        new("hail.quit", "Quit Hail", "Close Hail until it is started again", "\uE711", ["exit hail", "close hail"],
            _ =>
            {
                host.Quit();
                return ValueTask.CompletedTask;
            },
            Question: null),
    ];

    private static Func<CancellationToken, ValueTask> Run(Action action) =>
        _ =>
        {
            action();
            return ValueTask.CompletedTask;
        };

    /// <param name="Question">Asked before running it, or null to run it at once.</param>
    private sealed record Command(
        string Id,
        string Title,
        string Subtitle,
        string Glyph,
        IReadOnlyList<string> Aliases,
        Func<CancellationToken, ValueTask> Execute,
        string? Question)
    {
        public Result ToResult(double relevance, MatchSpans? highlight)
        {
            var run = new ResultAction(Title, Gesture.Enter, async (_, ct) =>
            {
                await Execute(ct).ConfigureAwait(false);
                return ActionOutcome.Hide;
            });

            var primary = Question is null
                ? run
                : new ResultAction(Title, Gesture.Enter, (_, _) => ValueTask.FromResult(ActionOutcome.AskFirst(Question, run)));

            return new Result(Id, Title, Subtitle, IconSource.ForGlyph(Glyph), relevance, primary, Secondary: [], highlight);
        }
    }
}
