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
/// The provider's own judgement, 0 to 1. The host clamps anything outside that range.
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
    MatchSpans? Highlight);

/// <summary>Something a result can do.</summary>
/// <param name="Title">"Open", "Run as administrator", "Copy result".</param>
/// <param name="Gesture">The chord that runs it; the row names it.</param>
/// <param name="Execute">The action itself.</param>
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
    Enter,
    CtrlEnter,
    ShiftEnter,
    CtrlShiftEnter,
    CtrlC,
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
    public static ActionOutcome ReplaceQuery(string text) => new ReplaceQueryText(text);

    public sealed record HideBox : ActionOutcome;

    public sealed record KeepBoxOpen : ActionOutcome;

    public sealed record ReplaceQueryText(string Text) : ActionOutcome;
}
