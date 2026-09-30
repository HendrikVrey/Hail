namespace Hail.Sdk;

/// <summary>One row in the box.</summary>
/// <param name="Id">
/// Stable per provider: the key a choice is remembered under, so the same thing must get the
/// same id on every query.
/// </param>
/// <param name="Title">What the row says.</param>
/// <param name="Subtitle">A muted second line, or null.</param>
/// <param name="Icon">Data describing the icon, never an image object.</param>
/// <param name="Relevance">
/// The provider's own judgement, 0 to 1. The host clamps anything outside that range. A row
/// at <see cref="LastResort"/> or below is a last resort (<em>Search the web for...</em>): the
/// host shows it only once every provider has answered, and below everything else.
/// </param>
/// <param name="Primary">What Enter does.</param>
/// <param name="Secondary">Other actions, each on its own chord.</param>
/// <param name="Highlight">Which characters of <paramref name="Title"/> matched, for bolding.</param>
public sealed record Result(
    string Id,
    string Title,
    string? Subtitle,
    IconSource Icon,
    double Relevance,
    ResultAction Primary,
    IReadOnlyList<ResultAction> Secondary,
    MatchSpans? Highlight)
{
    /// <summary>The relevance at or below which a row is a last resort (see <see cref="Relevance"/>).</summary>
    public const double LastResort = 0.02;

    /// <summary>
    /// What Tab puts in the box when this row is highlighted: the next folder of a path, a
    /// calculator's answer. Null when Tab has nothing to complete here.
    /// </summary>
    public string? Completion { get; init; }
}

/// <summary>Something a result can do.</summary>
/// <param name="Title">"Open", "Run as administrator", "Copy result".</param>
/// <param name="Gesture">The chord that runs it; the row names it.</param>
/// <param name="Execute">
/// The action itself. Runs on a background thread. The host waits for it a few seconds at most
/// (a launch behind Windows' own elevation prompt included), then cancels the token, stops
/// waiting and says so in the box; an action that ignores the token finishes into nothing.
/// </param>
public sealed record ResultAction(
    string Title,
    Gesture Gesture,
    Func<IActionContext, CancellationToken, ValueTask<ActionOutcome>> Execute);

/// <summary>What an action is told when it runs.</summary>
public interface IActionContext
{
    /// <summary>The query the result was produced for.</summary>
    Query Query { get; }
}

/// <summary>The chords an action can be bound to.</summary>
public enum Gesture
{
    /// <summary>Enter, or a click: the primary action.</summary>
    Enter,

    /// <summary>Ctrl+Enter.</summary>
    CtrlEnter,

    /// <summary>Shift+Enter.</summary>
    ShiftEnter,

    /// <summary>Ctrl+Shift+Enter; by convention, as administrator.</summary>
    CtrlShiftEnter,

    /// <summary>Ctrl+C with nothing selected in the box; by convention, copy the path or text.</summary>
    CtrlC,

    /// <summary>Ctrl+Shift+C; by convention, copy the file itself.</summary>
    CtrlShiftC,
}

/// <summary>What the box does after an action has run.</summary>
public abstract record ActionOutcome
{
    private ActionOutcome()
    {
    }

    /// <summary>The usual: the box goes away.</summary>
    public static ActionOutcome Hide { get; } = new HideBox();

    /// <summary>The box stays, as it was.</summary>
    public static ActionOutcome KeepOpen { get; } = new KeepBoxOpen();

    /// <summary>The box stays and its text becomes <paramref name="text"/>.</summary>
    public static ActionOutcome ReplaceQuery(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ReplaceQueryText(text);
    }

    /// <summary>
    /// Nothing has happened yet: the box asks <paramref name="question"/> and runs
    /// <paramref name="confirmed"/> only if the user presses Enter on it. Escape, or typing,
    /// goes back. For anything that cannot be taken back (restarting Windows, say).
    /// </summary>
    public static ActionOutcome AskFirst(string question, ResultAction confirmed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(confirmed);
        return new Confirm(question, confirmed);
    }

    /// <summary>The box goes away: <see cref="Hide"/>.</summary>
    public sealed record HideBox : ActionOutcome;

    /// <summary>The box stays: <see cref="KeepOpen"/>.</summary>
    public sealed record KeepBoxOpen : ActionOutcome;

    /// <summary>The box's text changes: <see cref="ReplaceQuery"/>.</summary>
    /// <param name="Text">The new text.</param>
    public sealed record ReplaceQueryText(string Text) : ActionOutcome;

    /// <summary>The box asks first: <see cref="AskFirst"/>.</summary>
    /// <param name="Question">What the box asks.</param>
    /// <param name="Confirmed">What runs if the user says yes.</param>
    public sealed record Confirm(string Question, ResultAction Confirmed) : ActionOutcome;
}
