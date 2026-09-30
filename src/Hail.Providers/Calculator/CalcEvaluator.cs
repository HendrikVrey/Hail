namespace Hail.Providers.Calculator;

/// <summary>
/// Works a parsed sum out. It evaluates arithmetic and a fixed list of functions and nothing
/// else: there is no code here that could be reached by what was typed (Hail.md §7.3).
/// </summary>
/// <remarks>
/// Arithmetic is <see cref="decimal"/> and falls back to <see cref="double"/> only when a
/// result leaves decimal's range (an overflow above, an underflow to zero below), so a huge
/// power is still answered in scientific notation and only an infinite one is refused.
/// </remarks>
internal static class CalcEvaluator
{
    private const decimal Pi = 3.1415926535897932384626433833m;
    private const decimal E = 2.7182818284590452353602874714m;

    /// <summary>Trigonometry is rounded to this many places, so sin(pi) is 0 and not 1.2E-16.</summary>
    private const int TrigonometryPlaces = 15;

    public static CalcValue Evaluate(Node node) => node switch
    {
        NumberNode number => number.Value,
        ConstantNode constant => CalcValue.Of(constant.Name == "pi" ? Pi : E, exact: false),
        NegateNode negate => Negate(Evaluate(negate.Operand)),
        PercentNode percent => Divide(Evaluate(percent.Operand), CalcValue.Of(100)),
        BinaryNode binary => Binary(binary.Operator, Evaluate(binary.Left), Evaluate(binary.Right)),
        CallNode call => Call(call.Name, [.. call.Arguments.Select(Evaluate)]),
        _ => throw new InvalidOperationException($"No evaluation for {node.GetType().Name}."),
    };

    private static CalcValue Negate(CalcValue value) =>
        value.IsDecimal ? value with { Decimal = -value.Decimal } : value with { Double = -value.Double };

    private static CalcValue Binary(char op, CalcValue left, CalcValue right) => op switch
    {
        '+' => Arithmetic(left, right, (a, b) => a + b, (a, b) => a + b),
        '-' => Arithmetic(left, right, (a, b) => a - b, (a, b) => a - b),
        '*' => Multiply(left, right),
        '/' => Divide(left, right),
        '%' => Remainder(left, right),
        '^' => Power(left, right),
        _ => throw new InvalidOperationException($"No operator '{op}'."),
    };

    private static CalcValue Arithmetic(CalcValue left, CalcValue right, Func<decimal, decimal, decimal> exact, Func<double, double, double> approximate)
    {
        if (left.IsDecimal && right.IsDecimal)
        {
            try
            {
                return CalcValue.Of(exact(left.Decimal, right.Decimal), left.Exact && right.Exact);
            }
            catch (OverflowException)
            {
                // Past decimal's range: answered as a double below.
            }
        }

        return CalcValue.FromDouble(approximate(left.AsDouble, right.AsDouble));
    }

    private static CalcValue Multiply(CalcValue left, CalcValue right)
    {
        if (left.IsDecimal && right.IsDecimal)
        {
            try
            {
                var product = left.Decimal * right.Decimal;
                if (product != 0 || left.Decimal == 0 || right.Decimal == 0)
                {
                    return CalcValue.Of(product, left.Exact && right.Exact);
                }

                // Two non-zero numbers whose product decimal rounded to zero: an underflow.
            }
            catch (OverflowException)
            {
                // Past decimal's range: answered as a double below.
            }
        }

        return CalcValue.FromDouble(left.AsDouble * right.AsDouble);
    }

    private static CalcValue Divide(CalcValue left, CalcValue right)
    {
        if (right.IsZero)
        {
            throw new CalcRefusalException("Cannot divide by zero.");
        }

        if (left.IsDecimal && right.IsDecimal)
        {
            try
            {
                var quotient = left.Decimal / right.Decimal;
                if (quotient != 0 || left.Decimal == 0)
                {
                    return CalcValue.Of(quotient, left.Exact && right.Exact && DividesExactly(quotient, right.Decimal, left.Decimal));
                }
            }
            catch (OverflowException)
            {
                // Past decimal's range: answered as a double below.
            }
        }

        return CalcValue.FromDouble(left.AsDouble / right.AsDouble);
    }

    /// <summary>Whether a quotient is the whole answer and not a rounding of it (1/3 is not).</summary>
    private static bool DividesExactly(decimal quotient, decimal divisor, decimal dividend)
    {
        try
        {
            return quotient * divisor == dividend;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static CalcValue Remainder(CalcValue left, CalcValue right)
    {
        if (right.IsZero)
        {
            throw new CalcRefusalException("Cannot divide by zero.");
        }

        return left.IsDecimal && right.IsDecimal
            ? CalcValue.Of(left.Decimal % right.Decimal, left.Exact && right.Exact)
            : CalcValue.FromDouble(left.AsDouble % right.AsDouble);
    }

    private static CalcValue Power(CalcValue @base, CalcValue exponent)
    {
        var isWhole = exponent.IsDecimal ? exponent.Decimal == decimal.Truncate(exponent.Decimal) : Math.Floor(exponent.Double) == exponent.Double;

        if (@base.IsZero && exponent.IsNegative)
        {
            throw new CalcRefusalException("Cannot divide by zero.");
        }

        if (@base.IsNegative && !isWhole)
        {
            throw new CalcRefusalException("A negative number has no real fractional power.");
        }

        if (isWhole && @base.IsDecimal && exponent.IsDecimal && Math.Abs(exponent.Decimal) <= ulong.MaxValue)
        {
            var whole = WholePower(@base, (ulong)Math.Abs(exponent.Decimal));
            if (whole is { } power)
            {
                return exponent.IsNegative ? Divide(CalcValue.Of(1), power) : power;
            }
        }

        // Math.Pow is exact where the double can be; 9^9^9 comes back infinite and is refused.
        return CalcValue.FromDouble(Math.Pow(@base.AsDouble, exponent.AsDouble));
    }

    /// <summary>
    /// A decimal power by repeated squaring: as many multiplications as the exponent has bits.
    /// Null when the answer leaves decimal's range either way, for the double to answer.
    /// </summary>
    private static CalcValue? WholePower(CalcValue @base, ulong exponent)
    {
        var result = 1m;
        var square = @base.Decimal;
        try
        {
            while (exponent > 0)
            {
                if ((exponent & 1) == 1)
                {
                    result *= square;
                }

                exponent >>= 1;
                if (exponent > 0)
                {
                    square *= square;
                }
            }
        }
        catch (OverflowException)
        {
            return null;
        }

        if (result == 0 && @base.Decimal != 0)
        {
            return null; // underflowed
        }

        return CalcValue.Of(result, @base.Exact);
    }

    private static CalcValue Call(string name, IReadOnlyList<CalcValue> arguments)
    {
        switch (name)
        {
            case "sqrt":
                return SquareRoot(One(name, arguments));

            case "abs":
            {
                var x = One(name, arguments);
                return x.IsDecimal ? x with { Decimal = Math.Abs(x.Decimal) } : x with { Double = Math.Abs(x.Double) };
            }

            case "floor":
            {
                var x = One(name, arguments);
                return x.IsDecimal ? x with { Decimal = decimal.Floor(x.Decimal) } : CalcValue.FromDouble(Math.Floor(x.Double));
            }

            case "ceil":
            {
                var x = One(name, arguments);
                return x.IsDecimal ? x with { Decimal = decimal.Ceiling(x.Decimal) } : CalcValue.FromDouble(Math.Ceiling(x.Double));
            }

            case "round":
                return Round(arguments);

            case "min":
            case "max":
            {
                if (arguments.Count == 0)
                {
                    throw new CalcRefusalException($"{name} needs at least one number.");
                }

                var pick = arguments[0];
                foreach (var x in arguments.Skip(1))
                {
                    var less = Compare(x, pick) < 0;
                    if (name == "min" ? less : Compare(x, pick) > 0)
                    {
                        pick = x;
                    }
                }

                return pick;
            }

            case "sin":
                return Trigonometry(Math.Sin(One(name, arguments).AsDouble));

            case "cos":
                return Trigonometry(Math.Cos(One(name, arguments).AsDouble));

            case "tan":
                return Trigonometry(Math.Tan(One(name, arguments).AsDouble));

            case "log":
                return CalcValue.FromDouble(Math.Log10(Positive(name, One(name, arguments))));

            case "ln":
                return CalcValue.FromDouble(Math.Log(Positive(name, One(name, arguments))));

            case "exp":
                return CalcValue.FromDouble(Math.Exp(One(name, arguments).AsDouble));

            default:
                throw new InvalidOperationException($"No function '{name}'.");
        }
    }

    private static CalcValue One(string name, IReadOnlyList<CalcValue> arguments) =>
        arguments.Count == 1 ? arguments[0] : throw new CalcRefusalException($"{name} takes one number.");

    private static int Compare(CalcValue a, CalcValue b) =>
        a.IsDecimal && b.IsDecimal ? a.Decimal.CompareTo(b.Decimal) : a.AsDouble.CompareTo(b.AsDouble);

    private static double Positive(string name, CalcValue x) =>
        x.IsZero || x.IsNegative
            ? throw new CalcRefusalException($"{name} of zero or a negative number is not defined.")
            : x.AsDouble;

    private static CalcValue SquareRoot(CalcValue x)
    {
        if (x.IsNegative)
        {
            throw new CalcRefusalException("A negative number has no real square root.");
        }

        var root = Math.Sqrt(x.AsDouble);
        if (x.IsDecimal && x.Exact && root < 7.9e28)
        {
            // sqrt(16) is 4 exactly, and says so.
            var whole = (decimal)root;
            try
            {
                if (whole * whole == x.Decimal)
                {
                    return CalcValue.Of(whole);
                }
            }
            catch (OverflowException)
            {
                // Not exact; the double stands.
            }
        }

        return CalcValue.FromDouble(root);
    }

    private static CalcValue Round(IReadOnlyList<CalcValue> arguments)
    {
        if (arguments.Count is not (1 or 2))
        {
            throw new CalcRefusalException("round takes a number, and how many decimal places to keep.");
        }

        var places = 0;
        if (arguments.Count == 2)
        {
            var requested = arguments[1].AsDouble;
            if (requested != Math.Floor(requested) || requested < 0 || requested > 28)
            {
                throw new CalcRefusalException("round keeps a whole number of places, from 0 to 28.");
            }

            places = (int)requested;
        }

        var x = arguments[0];
        return x.IsDecimal
            ? x with { Decimal = decimal.Round(x.Decimal, places, MidpointRounding.AwayFromZero) }
            : CalcValue.FromDouble(Math.Round(x.Double, Math.Min(places, 15), MidpointRounding.AwayFromZero));
    }

    private static CalcValue Trigonometry(double value) =>
        CalcValue.FromDouble(Math.Abs(value) < 1e6 ? Math.Round(value, TrigonometryPlaces) : value);
}
