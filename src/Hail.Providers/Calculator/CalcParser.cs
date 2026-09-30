namespace Hail.Providers.Calculator;

internal abstract record Node;

internal sealed record NumberNode(CalcValue Value) : Node;

internal sealed record ConstantNode(string Name) : Node;

internal sealed record NegateNode(Node Operand) : Node;

/// <param name="Operator">One of <c>+ - * / % ^</c>.</param>
/// <param name="Spelling">How the operator was written, for the echo: "of" after a percentage.</param>
internal sealed record BinaryNode(char Operator, Node Left, Node Right, string? Spelling = null) : Node;

internal sealed record PercentNode(Node Operand) : Node;

internal sealed record CallNode(string Name, IReadOnlyList<Node> Arguments) : Node;

/// <summary>
/// A Pratt parser over the calculator's tokens (Hail.md §7.3). Precedence, loosest first:
/// <c>+ -</c>; <c>* / %</c>, implicit multiplication (<c>2pi</c>, <c>3(4+5)</c>) and
/// <c>of</c> after a percentage; unary minus; <c>^</c>, which associates to the right, so
/// <c>-2^2</c> is <c>-4</c> and <c>2^3^2</c> is <c>512</c>; a postfix percentage.
/// </summary>
/// <remarks>
/// Two numbers side by side are refused rather than multiplied, because "1 000" is someone's
/// thousand and not zero. A <c>%</c> followed by something to divide by is the remainder; any
/// other is a percentage.
/// </remarks>
internal sealed class CalcParser
{
    public const int MaxDepth = 32;

    public static readonly IReadOnlySet<string> Functions = new HashSet<string>(StringComparer.Ordinal)
    {
        "sqrt", "abs", "round", "floor", "ceil", "min", "max", "sin", "cos", "tan", "log", "ln", "exp",
    };

    public static readonly IReadOnlySet<string> Constants = new HashSet<string>(StringComparer.Ordinal) { "pi", "e" };

    private const int AdditiveBp = 10;
    private const int MultiplicativeBp = 20;
    private const int UnaryBp = 25;
    private const int PowerBp = 30;
    private const int PercentBp = 40;

    private readonly List<Token> _tokens;
    private int _position;

    private CalcParser(List<Token> tokens) => _tokens = tokens;

    private Token Current => _tokens[_position];

    public static Node Parse(List<Token> tokens)
    {
        var parser = new CalcParser(tokens);
        var node = parser.Expression(0, 0);
        if (parser.Current.Kind != TokenKind.End)
        {
            throw parser.Current.Kind switch
            {
                TokenKind.Number => new CalcSyntaxException("Two numbers need an operator between them."),
                TokenKind.RightParen => new CalcSyntaxException("A bracket is closed that was never opened."),
                _ => new CalcSyntaxException($"Hail did not expect “{parser.Current.Text}” there."),
            };
        }

        return node;
    }

    private static string Lower(Token token) => token.Text.ToLowerInvariant();

    private Token Advance() => _tokens[_position++];

    private Token Peek(int ahead) => _tokens[Math.Min(_position + ahead, _tokens.Count - 1)];

    private Node Expression(int minBp, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new CalcSyntaxException("That sum is nested too deeply.");
        }

        var left = Prefix(depth);
        while (true)
        {
            var token = Current;
            switch (token.Kind)
            {
                case TokenKind.Plus or TokenKind.Minus when AdditiveBp > minBp:
                    Advance();
                    left = new BinaryNode(token.Kind == TokenKind.Plus ? '+' : '-', left, Expression(AdditiveBp, depth + 1));
                    continue;

                case TokenKind.Star or TokenKind.Slash when MultiplicativeBp > minBp:
                    Advance();
                    left = new BinaryNode(token.Kind == TokenKind.Star ? '*' : '/', left, Expression(MultiplicativeBp, depth + 1));
                    continue;

                case TokenKind.Caret when PowerBp > minBp:
                    Advance();
                    left = new BinaryNode('^', left, Expression(PowerBp - 1, depth + 1));
                    continue;

                case TokenKind.Percent when IsRemainder():
                    if (MultiplicativeBp <= minBp)
                    {
                        return left;
                    }

                    Advance();
                    left = new BinaryNode('%', left, Expression(MultiplicativeBp, depth + 1));
                    continue;

                case TokenKind.Percent when PercentBp > minBp:
                    Advance();
                    left = new PercentNode(left);
                    continue;

                case TokenKind.Identifier when Lower(token) == "of":
                    if (left is not PercentNode)
                    {
                        throw new CalcSyntaxException("“of” only follows a percentage, as in 15% of 240.");
                    }

                    if (MultiplicativeBp <= minBp)
                    {
                        return left;
                    }

                    Advance();
                    left = new BinaryNode('*', left, Expression(MultiplicativeBp, depth + 1), Spelling: "of");
                    continue;

                // Implicit multiplication, before a name or a bracket only (see remarks).
                case TokenKind.Identifier or TokenKind.LeftParen when MultiplicativeBp > minBp:
                    left = new BinaryNode('*', left, Expression(MultiplicativeBp, depth + 1));
                    continue;

                default:
                    return left;
            }
        }
    }

    /// <summary>A <c>%</c> with something after it to divide by is a remainder, not a percentage.</summary>
    private bool IsRemainder()
    {
        var next = Peek(1);
        return next.Kind is TokenKind.Number or TokenKind.LeftParen
            || (next.Kind == TokenKind.Identifier && Lower(next) != "of");
    }

    private Node Prefix(int depth)
    {
        var token = Advance();
        switch (token.Kind)
        {
            case TokenKind.Number:
                return new NumberNode(token.Value);

            case TokenKind.Minus:
                return new NegateNode(Expression(UnaryBp, depth + 1));

            case TokenKind.Plus:
                return Expression(UnaryBp, depth + 1);

            case TokenKind.LeftParen:
            {
                var inner = Expression(0, depth + 1);
                Expect(TokenKind.RightParen, "A bracket is not closed.");
                return inner;
            }

            case TokenKind.Identifier:
                return Name(token, depth);

            case TokenKind.End:
                throw new CalcSyntaxException("The sum is not finished.");

            default:
                throw new CalcSyntaxException($"Hail did not expect “{token.Text}” there.");
        }
    }

    private Node Name(Token token, int depth)
    {
        var name = Lower(token) switch
        {
            "\u03C0" => "pi",
            var other => other,
        };

        if (Functions.Contains(name))
        {
            if (Current.Kind != TokenKind.LeftParen)
            {
                throw new CalcSyntaxException($"{name} needs its number in brackets, as in {name}(2).");
            }

            Advance();
            var arguments = new List<Node>();
            if (Current.Kind != TokenKind.RightParen)
            {
                arguments.Add(Expression(0, depth + 1));
                while (Current.Kind == TokenKind.Separator)
                {
                    Advance();
                    arguments.Add(Expression(0, depth + 1));
                }
            }

            Expect(TokenKind.RightParen, "A bracket is not closed.");
            return new CallNode(name, arguments);
        }

        if (Constants.Contains(name))
        {
            return new ConstantNode(name);
        }

        throw new CalcSyntaxException($"Hail does not know “{token.Text}”.");
    }

    private void Expect(TokenKind kind, string otherwise)
    {
        if (Current.Kind != kind)
        {
            throw new CalcSyntaxException(otherwise);
        }

        Advance();
    }
}
