using System.Globalization;

namespace Hail.Providers.Calculator;

/// <summary>
/// A number as the calculator carries it: a <see cref="decimal"/> wherever one can hold it, so
/// that <c>0.1 + 0.2</c> is <c>0.3</c>, and a <see cref="double"/> only past decimal's range or
/// out of a function that needs one. <see cref="Exact"/> says whether anything was rounded on
/// the way, and the answer is shown with no more digits than it has.
/// </summary>
internal readonly record struct CalcValue(decimal Decimal, double Double, bool IsDecimal, bool Exact)
{
    /// <summary>Doubles are shown to this many significant digits, which is what they carry.</summary>
    public const int DoubleDigits = 15;

    // Inside this range a double is carried as a decimal; outside it, as itself.
    private const double DecimalCeiling = 7.9e28;
    private const double DecimalFloor = 1e-20;

    /// <summary>Decimal holds 28 or 29 significant digits; at 28 a result may already be rounded.</summary>
    private const int MaxExactDigits = 28;

    public double AsDouble => IsDecimal ? (double)Decimal : Double;

    public bool IsZero => IsDecimal ? Decimal == 0 : Double == 0;

    public bool IsNegative => IsDecimal ? Decimal < 0 : Double < 0;

    /// <summary>
    /// A decimal, exact unless told otherwise or unless it has as many digits as a decimal can
    /// hold: past that, decimal arithmetic rounds without saying so (<c>1e28 + 0.4</c> loses the
    /// 0.4), and the answer must not claim a precision it does not have.
    /// </summary>
    public static CalcValue Of(decimal value, bool exact = true) =>
        new(value, 0, IsDecimal: true, exact && Digits(value) < MaxExactDigits);

    /// <summary>The digits of the decimal's integer mantissa, trailing zeros included.</summary>
    private static int Digits(decimal value)
    {
        var bits = decimal.GetBits(value);
        var mantissa = ((UInt128)(uint)bits[2] << 64) | ((UInt128)(uint)bits[1] << 32) | (uint)bits[0];
        return mantissa == 0 ? 1 : mantissa.ToString(CultureInfo.InvariantCulture).Length;
    }

    /// <summary>A double from a function or an overflow; refused in a sentence if it is not a number.</summary>
    public static CalcValue FromDouble(double value)
    {
        if (double.IsNaN(value))
        {
            throw new CalcRefusalException("There is no real answer to that.");
        }

        if (double.IsInfinity(value))
        {
            throw new CalcRefusalException("The answer is too large to show.");
        }

        var magnitude = Math.Abs(value);
        if (magnitude == 0 || (magnitude < DecimalCeiling && magnitude >= DecimalFloor))
        {
            // Through the double's round-trip digits, not a cast: (decimal)double rounds to 15
            // significant digits itself, and rounding twice makes e end in 04 rather than 05.
            var digits = value.ToString("G17", CultureInfo.InvariantCulture);
            var converted = decimal.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture);
            return new CalcValue(converted, 0, IsDecimal: true, Exact: false);
        }

        return new CalcValue(0, value, IsDecimal: false, Exact: false);
    }

    /// <summary>
    /// The number in <paramref name="culture"/>'s format with no digit grouping, so it pastes
    /// back into anything: exact decimals in full, anything rounded to
    /// <see cref="DoubleDigits"/> significant digits.
    /// </summary>
    public string Format(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        if (!IsDecimal)
        {
            return Double.ToString("G15", culture);
        }

        var value = Exact ? Decimal : RoundSignificant(Decimal, DoubleDigits);
        if (value == 0)
        {
            value = 0m; // never "-0"
        }

        return value.ToString("0.############################", culture);
    }

    public static decimal RoundSignificant(decimal value, int digits)
    {
        if (value == 0)
        {
            return 0;
        }

        var magnitude = (int)Math.Floor(Math.Log10((double)Math.Abs(value))) + 1;
        var decimals = digits - magnitude;
        if (decimals >= 0)
        {
            return decimal.Round(value, Math.Min(decimals, 28), MidpointRounding.AwayFromZero);
        }

        var factor = 1m;
        for (var i = 0; i < -decimals; i++)
        {
            factor *= 10;
        }

        return decimal.Round(value / factor, 0, MidpointRounding.AwayFromZero) * factor;
    }
}

/// <summary>The text is not a sum Hail can read; the message is a sentence for the user.</summary>
internal sealed class CalcSyntaxException(string message) : Exception(message);

/// <summary>The sum reads, but Hail will not answer it; the message is a sentence for the user.</summary>
internal sealed class CalcRefusalException(string message) : Exception(message);
