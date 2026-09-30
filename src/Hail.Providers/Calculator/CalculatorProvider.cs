using System.Globalization;
using System.Runtime.CompilerServices;
using Hail.Sdk;

namespace Hail.Providers.Calculator;

/// <summary>
/// Answers sums as they are typed (Hail.md §7.3). Globally it speaks only when the whole query
/// is a sum with something to work out, so typing <c>2024</c> or an app's name offers nothing;
/// after <c>=</c> it always speaks, and says why when it cannot answer.
/// </summary>
/// <remarks>
/// Not an <see cref="IRecall"/>: an answer's id would be the sum that was typed, and history is
/// written to disk.
/// </remarks>
public sealed class CalculatorProvider(CultureInfo culture) : IProvider
{
    public const string ProviderId = "hail.calculator";
    public const string Keyword = "=";

    private const string CalculatorGlyph = "\uE8EF";

    private readonly CalculatorEngine _calculator = new(culture);
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
            ?? throw new InvalidOperationException($"{nameof(CalculatorProvider)} was queried before it was initialised.");
        ct.ThrowIfCancellationRequested();

        var result = Answer(query, context);
        if (result is not null)
        {
            yield return result;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private Result? Answer(Query query, IPluginContext context)
    {
        var asked = query.IsKeywordScoped;
        if (query.Search.Length == 0)
        {
            return asked ? Note("Type a sum", "Hail answers as you type: 15% of 240, 2^10, sqrt(2)") : null;
        }

        switch (_calculator.Evaluate(query.Search))
        {
            case CalcOutcome.Answer answer when asked || answer.HasOperator:
            {
                var again = asked ? $"{Keyword}{answer.Display}" : answer.Display;
                return new Result(
                    Id: "answer",
                    Title: answer.Display,
                    Subtitle: answer.IsApproximate ? $"{answer.Expression}  (rounded)" : answer.Expression,
                    Icon: IconSource.ForGlyph(CalculatorGlyph),
                    Relevance: asked ? 1.0 : 0.95,
                    Primary: new ResultAction("Copy", Gesture.Enter, async (_, ct) =>
                    {
                        await context.Clipboard.SetTextAsync(answer.Display, ct).ConfigureAwait(false);
                        return ActionOutcome.Hide;
                    }),
                    Secondary: [new ResultAction("Keep calculating", Gesture.CtrlEnter, (_, _) => ValueTask.FromResult(ActionOutcome.ReplaceQuery(again)))],
                    Highlight: null)
                {
                    Completion = again,
                };
            }

            case CalcOutcome.Refused refused when asked || refused.HasOperator:
                return Note(refused.Reason, refused.Expression);

            case CalcOutcome.NotASum notASum when asked:
                return Note(notASum.Reason, "Try 15% of 240, 2^10 or sqrt(2)");

            default:
                return null;
        }
    }

    /// <summary>A row that says something and does nothing: Enter leaves the box as it is.</summary>
    private static Result Note(string title, string subtitle) =>
        new(
            Id: "note",
            Title: title,
            Subtitle: subtitle,
            Icon: IconSource.ForGlyph(CalculatorGlyph),
            Relevance: 0.9,
            Primary: new ResultAction("Nothing to copy", Gesture.Enter, (_, _) => ValueTask.FromResult(ActionOutcome.KeepOpen)),
            Secondary: [],
            Highlight: null);
}
