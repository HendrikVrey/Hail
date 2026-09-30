using System.Globalization;

namespace Hail.Providers.Calculator;

internal enum TokenKind
{
    Number,
    Identifier,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Caret,
    LeftParen,
    RightParen,
    Separator,
    End,
}

internal readonly record struct Token(TokenKind Kind, int Position, string Text, CalcValue Value = default);

/// <summary>
/// The decimal point and argument separator the calculator reads (Hail.md §7.3). The point is
/// always accepted; a comma is also a decimal point where the culture writes one (this machine
/// writes <c>20,0</c>), and function arguments are then separated by <c>;</c>, as Excel does
/// in those cultures. Elsewhere <c>,</c> and <c>;</c> both separate arguments.
/// </summary>
internal sealed record CalcCulture(CultureInfo Culture)
{
    public bool CommaIsDecimal { get; } = Culture.NumberFormat.NumberDecimalSeparator == ",";

    public string ArgumentSeparator => CommaIsDecimal ? ";" : ",";

    public bool IsDecimalPoint(char c) => c == '.' || (c == ',' && CommaIsDecimal);

    public bool IsArgumentSeparator(char c) => c == ';' || (c == ',' && !CommaIsDecimal);
}

/// <summary>Turns the text into tokens. Every character it does not know is refused by name.</summary>
internal static class CalcLexer
{
    private const int MaxHexDigits = 16;
    private const int MaxBinaryDigits = 64;

    public static List<Token> Tokenise(string text, CalcCulture culture)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            if (char.IsAsciiDigit(c) || (culture.IsDecimalPoint(c) && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                tokens.Add(Number(text, ref i, culture));
                continue;
            }

            if (char.IsLetter(c))
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Identifier, start, text[start..i]));
                continue;
            }

            i++;
            var kind = c switch
            {
                '+' => TokenKind.Plus,
                '-' or '\u2212' => TokenKind.Minus,
                '*' when i < text.Length && text[i] == '*' => TokenKind.Caret,
                '*' or '\u00D7' or '\u22C5' => TokenKind.Star,
                '/' or '\u00F7' => TokenKind.Slash,
                '%' => TokenKind.Percent,
                '^' => TokenKind.Caret,
                '(' => TokenKind.LeftParen,
                ')' => TokenKind.RightParen,
                _ when culture.IsArgumentSeparator(c) => TokenKind.Separator,
                _ => throw new CalcSyntaxException($"Hail does not know what “{c}” means in a sum."),
            };

            if (kind == TokenKind.Caret && c == '*')
            {
                i++; // the second star of **
            }

            tokens.Add(new Token(kind, start, text[start..i]));
        }

        tokens.Add(new Token(TokenKind.End, text.Length, string.Empty));
        return tokens;
    }

    private static Token Number(string text, ref int i, CalcCulture culture)
    {
        var start = i;
        if (text[i] == '0' && i + 2 < text.Length)
        {
            var prefix = char.ToLowerInvariant(text[i + 1]);
            if (prefix == 'x' && char.IsAsciiHexDigit(text[i + 2]))
            {
                return Based(text, ref i, 16, MaxHexDigits, char.IsAsciiHexDigit, "hexadecimal");
            }

            if (prefix == 'b' && text[i + 2] is '0' or '1')
            {
                return Based(text, ref i, 2, MaxBinaryDigits, ch => ch is '0' or '1', "binary");
            }
        }

        var literal = new System.Text.StringBuilder();
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            literal.Append(text[i++]);
        }

        if (i + 1 < text.Length && culture.IsDecimalPoint(text[i]) && char.IsAsciiDigit(text[i + 1]))
        {
            literal.Append('.');
            i++;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                literal.Append(text[i++]);
            }
        }
        else if (i < text.Length && culture.IsDecimalPoint(text[i]) && literal.Length > 0 && (i + 1 == text.Length || !char.IsAsciiDigit(text[i + 1])))
        {
            // "5." reads as 5, as every calculator reads it.
            i++;
        }

        // An exponent only when a digit follows the e (or its sign), so "2e" stays 2 times e.
        if (i < text.Length && text[i] is 'e' or 'E')
        {
            var j = i + 1;
            if (j < text.Length && text[j] is '+' or '-')
            {
                j++;
            }

            if (j < text.Length && char.IsAsciiDigit(text[j]))
            {
                literal.Append('e').Append(text, i + 1, j - (i + 1));
                i = j;
                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    literal.Append(text[i++]);
                }
            }
        }

        var source = literal.ToString();
        const NumberStyles styles = NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        // A literal below decimal's smallest step (1e-30) parses as 0 rather than failing, so a
        // zero with a non-zero digit in it goes to the double instead.
        var mantissa = source.Split('e')[0];
        if (decimal.TryParse(source, styles, CultureInfo.InvariantCulture, out var exact)
            && (exact != 0 || !mantissa.Any(c => c is >= '1' and <= '9')))
        {
            return new Token(TokenKind.Number, start, text[start..i], CalcValue.Of(exact));
        }

        if (double.TryParse(source, styles, CultureInfo.InvariantCulture, out var approximate))
        {
            return new Token(TokenKind.Number, start, text[start..i], CalcValue.FromDouble(approximate));
        }

        throw new CalcSyntaxException("That number is too large to read.");
    }

    private static Token Based(string text, ref int i, int radix, int maxDigits, Func<char, bool> isDigit, string name)
    {
        var start = i;
        i += 2;
        var digitsStart = i;
        while (i < text.Length && isDigit(text[i]))
        {
            i++;
        }

        var digits = text[digitsStart..i];
        if (digits.Length > maxDigits)
        {
            throw new CalcSyntaxException($"That {name} number is too long; Hail reads up to {maxDigits} digits.");
        }

        var value = Convert.ToUInt64(digits, radix);
        return new Token(TokenKind.Number, start, text[start..i], CalcValue.Of(value));
    }
}
