using System.Globalization;
using System.Text;

namespace Hail.Providers.Calculator;

/// <summary>What the calculator made of some text.</summary>
public abstract record CalcOutcome
{
    private CalcOutcome()
    {
    }

    /// <summary>The text is not a sum; <paramref name="Reason"/> says why, for when the user asked for one.</summary>
    public sealed record NotASum(string Reason) : CalcOutcome;

    /// <param name="Display">The answer as the user's culture writes it; what is copied.</param>
    /// <param name="Expression">The sum as Hail read it, so the user can see what was worked out.</param>
    /// <param name="IsApproximate">True when anything was rounded on the way.</param>
    /// <param name="HasOperator">
    /// Whether anything was worked out at all: <c>2024</c> and <c>-5</c> have no operator, and
    /// the box offers no sum for them unless asked with <c>=</c>.
    /// </param>
    public sealed record Answer(string Display, string Expression, bool IsApproximate, bool HasOperator) : CalcOutcome;

    /// <summary>A sum Hail read but will not answer; <paramref name="Reason"/> is a sentence.</summary>
    public sealed record Refused(string Reason, string Expression, bool HasOperator) : CalcOutcome;
}

/// <summary>
/// The calculator of Hail.md §7.3: its own parser over arithmetic, a fixed list of functions,
/// <c>pi</c> and <c>e</c>, hexadecimal and binary, and percentages. It never evaluates code,
/// and bounds what it reads: <see cref="MaxLength"/> characters, <see cref="CalcParser.MaxDepth"/>
/// levels of nesting, and an answer that is not infinite. Stateless and safe to share.
/// </summary>
public sealed class CalculatorEngine
{
    public const int MaxLength = 256;

    private readonly CalcCulture _culture;

    public CalculatorEngine(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        _culture = new CalcCulture(culture);
    }

    public CalcOutcome Evaluate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return new CalcOutcome.NotASum("Type a sum.");
        }

        if (trimmed.Length > MaxLength)
        {
            return new CalcOutcome.NotASum($"That is longer than the {MaxLength} characters Hail will read as a sum.");
        }

        Node tree;
        try
        {
            tree = CalcParser.Parse(CalcLexer.Tokenise(trimmed, _culture));
        }
        catch (CalcSyntaxException syntax)
        {
            return new CalcOutcome.NotASum(syntax.Message);
        }
        catch (CalcRefusalException refusal)
        {
            // A number literal too large even for a double.
            return new CalcOutcome.NotASum(refusal.Message);
        }

        var expression = Print(tree);
        var hasOperator = HasOperator(tree);
        try
        {
            var value = CalcEvaluator.Evaluate(tree);
            return new CalcOutcome.Answer(value.Format(_culture.Culture), expression, !value.Exact, hasOperator);
        }
        catch (CalcRefusalException refusal)
        {
            return new CalcOutcome.Refused(refusal.Message, expression, hasOperator);
        }
    }

    private static bool HasOperator(Node node) => node switch
    {
        BinaryNode or PercentNode or CallNode => true,
        NegateNode negate => HasOperator(negate.Operand),
        _ => false,
    };

    private static int Precedence(Node node) => node switch
    {
        BinaryNode { Operator: '+' or '-' } => 1,
        BinaryNode { Operator: '*' or '/' or '%' } => 2,
        NegateNode => 3,
        BinaryNode { Operator: '^' } => 4,
        _ => 5,
    };

    /// <summary>The sum as it was read, with only the brackets it needs: <c>2pi</c> reads back as <c>2 × pi</c>.</summary>
    private string Print(Node node)
    {
        var text = new StringBuilder();
        Print(node, text);
        return text.ToString();
    }

    private void Print(Node node, StringBuilder text)
    {
        switch (node)
        {
            case NumberNode number:
                text.Append(number.Value.Format(_culture.Culture));
                break;

            case ConstantNode constant:
                text.Append(constant.Name);
                break;

            case NegateNode negate:
                text.Append('-');
                Wrapped(negate.Operand, Precedence(negate.Operand) < Precedence(negate), text);
                break;

            case PercentNode percent:
                Wrapped(percent.Operand, Precedence(percent.Operand) < 5, text);
                text.Append('%');
                break;

            case CallNode call:
                text.Append(call.Name).Append('(');
                for (var i = 0; i < call.Arguments.Count; i++)
                {
                    if (i > 0)
                    {
                        text.Append(_culture.ArgumentSeparator).Append(' ');
                    }

                    Print(call.Arguments[i], text);
                }

                text.Append(')');
                break;

            case BinaryNode binary:
            {
                var own = Precedence(binary);
                var rightAssociative = binary.Operator == '^';

                Wrapped(binary.Left, rightAssociative ? Precedence(binary.Left) <= own : Precedence(binary.Left) < own, text);
                text.Append(' ').Append(binary.Spelling ?? Symbol(binary.Operator)).Append(' ');
                Wrapped(binary.Right, rightAssociative ? Precedence(binary.Right) < own : Precedence(binary.Right) <= own && binary.Right is BinaryNode, text);
                break;
            }
        }
    }

    private void Wrapped(Node node, bool brackets, StringBuilder text)
    {
        if (brackets)
        {
            text.Append('(');
        }

        Print(node, text);

        if (brackets)
        {
            text.Append(')');
        }
    }

    private static string Symbol(char op) => op switch
    {
        '*' => "\u00D7",
        '/' => "\u00F7",
        '%' => "mod",
        _ => op.ToString(),
    };
}
