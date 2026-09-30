using System.Globalization;
using Hail.Providers.Calculator;

namespace Hail.Providers.Tests;

/// <summary>The calculator of Hail.md §7.3, in the invariant culture unless a test says otherwise.</summary>
public sealed class CalculatorTests
{
    private static readonly CalculatorEngine Invariant = new(CultureInfo.InvariantCulture);
    private static readonly CalculatorEngine Comma = new(CultureInfo.GetCultureInfo("de-DE"));

    private static CalcOutcome.Answer Answer(string text, CalculatorEngine? calculator = null) =>
        Assert.IsType<CalcOutcome.Answer>((calculator ?? Invariant).Evaluate(text));

    [Theory]
    [InlineData("1+2", "3")]
    [InlineData("0.1 + 0.2", "0.3")]
    [InlineData("2+3*4", "14")]
    [InlineData("2*3+4", "10")]
    [InlineData("(2+3)*4", "20")]
    [InlineData("10-4-3", "3")]
    [InlineData("100/10/5", "2")]
    [InlineData("2^3^2", "512")]
    [InlineData("-2^2", "-4")]
    [InlineData("(-2)^2", "4")]
    [InlineData("2^-1", "0.5")]
    [InlineData("10/4", "2.5")]
    [InlineData("15% of 240", "36")]
    [InlineData("50%", "0.5")]
    [InlineData("200 + 10%", "200.1")]
    [InlineData("10 % 3", "1")]
    [InlineData("10%3", "1")]
    [InlineData("3(4+5)", "27")]
    [InlineData("sqrt(16)", "4")]
    [InlineData("abs(-5)", "5")]
    [InlineData("floor(2.7)", "2")]
    [InlineData("ceil(2.1)", "3")]
    [InlineData("round(2.5)", "3")]
    [InlineData("round(2.345, 2)", "2.35")]
    [InlineData("round(2.345; 2)", "2.35")]
    [InlineData("min(3, 1, 2)", "1")]
    [InlineData("max(3;1;2)", "3")]
    [InlineData("0xFF + 1", "256")]
    [InlineData("0b1010 * 2", "20")]
    [InlineData("1e3 * 2", "2000")]
    [InlineData("2.5e-1 + 1", "1.25")]
    [InlineData("2^64", "18446744073709551616")]
    [InlineData("3 \u00D7 4", "12")]
    [InlineData("8 \u00F7 2", "4")]
    [InlineData("5 \u2212 2", "3")]
    [InlineData("2**10", "1024")]
    [InlineData("5. + 1", "6")]
    [InlineData(".5 + 1", "1.5")]
    [InlineData("-(1+2)", "-3")]
    public void Works_it_out_exactly(string text, string display)
    {
        var answer = Answer(text);

        Assert.Equal(display, answer.Display);
        Assert.False(answer.IsApproximate, $"{text} should be exact");
        Assert.True(answer.HasOperator);
    }

    [Theory]
    [InlineData("1/3", "0.333333333333333")]
    [InlineData("1/3*3", "1")]
    [InlineData("2pi", "6.28318530717959")]
    [InlineData("2\u03C0", "6.28318530717959")]
    [InlineData("sqrt(2)", "1.4142135623731")]
    [InlineData("sin(pi)", "0")]
    [InlineData("cos(0)", "1")]
    [InlineData("log(1000)", "3")]
    [InlineData("ln(e)", "1")]
    [InlineData("exp(1)", "2.71828182845905")]
    [InlineData("2^100", "1.26765060022823E+30")]
    [InlineData("10^40 * 10^40", "1E+80")]
    [InlineData("0.1^40", "1E-40")]
    public void Says_when_it_rounded_and_shows_no_false_precision(string text, string display)
    {
        var answer = Answer(text);

        Assert.Equal(display, answer.Display);
        Assert.True(answer.IsApproximate, $"{text} should be approximate");
    }

    [Theory]
    [InlineData("1/0", "Cannot divide by zero.")]
    [InlineData("5 % 0", "Cannot divide by zero.")]
    [InlineData("0^-1", "Cannot divide by zero.")]
    [InlineData("9^9^9", "The answer is too large to show.")]
    [InlineData("exp(100000)", "The answer is too large to show.")]
    [InlineData("sqrt(-1)", "A negative number has no real square root.")]
    [InlineData("(-8)^(1/3)", "A negative number has no real fractional power.")]
    [InlineData("log(0)", "log of zero or a negative number is not defined.")]
    [InlineData("ln(-1)", "ln of zero or a negative number is not defined.")]
    [InlineData("round(1; 2; 3)", "round takes a number, and how many decimal places to keep.")]
    [InlineData("round(1.5; 0.5)", "round keeps a whole number of places, from 0 to 28.")]
    [InlineData("min()", "min needs at least one number.")]
    [InlineData("sqrt(1, 2)", "sqrt takes one number.")]
    public void Refuses_in_a_sentence(string text, string reason)
    {
        var refused = Assert.IsType<CalcOutcome.Refused>(Invariant.Evaluate(text));
        Assert.Equal(reason, refused.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("notepad")]
    [InlineData("7-zip")]
    [InlineData("1password")]
    [InlineData(@"C:\Users")]
    [InlineData("2 3")]
    [InlineData("1 000")]
    [InlineData("(1+2")]
    [InlineData("1+2)")]
    [InlineData("1+")]
    [InlineData("sqrt 4")]
    [InlineData("2 of 3")]
    [InlineData("foo(2)")]
    [InlineData("0x")]
    [InlineData("0x12345678901234567")]
    [InlineData("2 & 3")]
    public void Anything_that_is_not_a_sum_is_said_to_be_so(string text)
    {
        var notASum = Assert.IsType<CalcOutcome.NotASum>(Invariant.Evaluate(text));
        Assert.False(string.IsNullOrWhiteSpace(notASum.Reason));
    }

    [Fact]
    public void Two_numbers_side_by_side_are_refused_not_multiplied()
    {
        var notASum = Assert.IsType<CalcOutcome.NotASum>(Invariant.Evaluate("1 000"));
        Assert.Equal("Two numbers need an operator between them.", notASum.Reason);
    }

    [Fact]
    public void What_it_reads_is_bounded()
    {
        Assert.IsType<CalcOutcome.NotASum>(Invariant.Evaluate(string.Join('+', Enumerable.Repeat("1", 200))));

        var deep = new string('(', 40) + "1" + new string(')', 40);
        Assert.Equal("That sum is nested too deeply.", Assert.IsType<CalcOutcome.NotASum>(Invariant.Evaluate(deep)).Reason);

        var powers = string.Join('^', Enumerable.Repeat("2", 60));
        Assert.IsType<CalcOutcome.NotASum>(Invariant.Evaluate(powers));
    }

    [Theory]
    [InlineData("2024", false)]
    [InlineData("-5", false)]
    [InlineData("pi", false)]
    [InlineData("(7)", false)]
    [InlineData("0b1010", false)]
    [InlineData("15%", true)]
    [InlineData("sqrt(4)", true)]
    [InlineData("2pi", true)]
    [InlineData("-(1+2)", true)]
    public void Knows_whether_anything_was_worked_out(string text, bool hasOperator)
    {
        Assert.Equal(hasOperator, Answer(text).HasOperator);
    }

    [Theory]
    [InlineData("2+3*4", "2 + 3 \u00D7 4")]
    [InlineData("(2+3)*4", "(2 + 3) \u00D7 4")]
    [InlineData("2^3^2", "2 ^ 3 ^ 2")]
    [InlineData("(2^3)^2", "(2 ^ 3) ^ 2")]
    [InlineData("10-(4-3)", "10 - (4 - 3)")]
    [InlineData("2pi", "2 \u00D7 pi")]
    [InlineData("15% of 240", "15% of 240")]
    [InlineData("10%3", "10 mod 3")]
    [InlineData("-(1+2)", "-(1 + 2)")]
    [InlineData("max(1,2)", "max(1, 2)")]
    [InlineData("8/2", "8 \u00F7 2")]
    public void Reads_the_sum_back_as_it_understood_it(string text, string expression)
    {
        Assert.Equal(expression, Answer(text).Expression);
    }

    [Theory]
    [InlineData("1,5+1", "2,5")]
    [InlineData("0,1 + 0,2", "0,3")]
    [InlineData("1.5+1", "2,5")]
    [InlineData("round(2,345; 2)", "2,35")]
    [InlineData("max(1;2)", "2")]
    [InlineData("1/4", "0,25")]
    public void In_a_culture_that_writes_a_comma_the_comma_is_the_point(string text, string display)
    {
        Assert.Equal(display, Answer(text, Comma).Display);
    }

    [Fact]
    public void In_a_comma_culture_arguments_are_separated_by_semicolons_and_read_back_so()
    {
        Assert.Equal("round(2,345; 2)", Answer("round(2,345; 2)", Comma).Expression);
        Assert.IsType<CalcOutcome.NotASum>(Comma.Evaluate("max(1, 2)"));
    }

    [Fact]
    public void A_negative_zero_is_just_zero()
    {
        Assert.Equal("0", Answer("-0 * 5").Display);
    }
}
