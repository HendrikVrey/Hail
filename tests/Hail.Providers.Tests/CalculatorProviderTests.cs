using System.Globalization;
using Hail.Providers.Calculator;
using Hail.Sdk;
using static Hail.Providers.Tests.Harness;

namespace Hail.Providers.Tests;

public sealed class CalculatorProviderTests
{
    private readonly FakeClipboard _clipboard = new();

    private async Task<CalculatorProvider> StartedAsync()
    {
        var provider = new CalculatorProvider(CultureInfo.InvariantCulture);
        await provider.InitializeAsync(Context(new FakeLauncher(), _clipboard), Token);
        return provider;
    }

    [Fact]
    public async Task A_sum_typed_anywhere_is_answered_and_enter_copies_it()
    {
        var result = Assert.Single(await CollectAsync(await StartedAsync(), Query.Global("15*4+2")));

        Assert.Equal("62", result.Title);
        Assert.Equal("15 × 4 + 2", result.Subtitle);
        Assert.Equal(ActionOutcome.Hide, await Run(result.Primary));
        Assert.Equal(["text 62"], _clipboard.Calls);
    }

    [Fact]
    public async Task Ctrl_enter_and_tab_carry_on_from_the_answer()
    {
        var result = Assert.Single(await CollectAsync(await StartedAsync(), Query.Global("2+2")));

        Assert.Equal(ActionOutcome.ReplaceQuery("4"), await Run(On(result, Gesture.CtrlEnter)));
        Assert.Equal("4", result.Completion);
    }

    [Fact]
    public async Task After_equals_carrying_on_keeps_the_equals()
    {
        var result = Assert.Single(await CollectAsync(await StartedAsync(), Scoped("=", "2+2")));

        Assert.Equal(ActionOutcome.ReplaceQuery("=4"), await Run(On(result, Gesture.CtrlEnter)));
    }

    [Theory]
    [InlineData("2024")]
    [InlineData("notepad")]
    [InlineData("pi")]
    [InlineData("-5")]
    [InlineData("")]
    public async Task Typed_anywhere_it_says_nothing_unless_something_was_worked_out(string text)
    {
        Assert.Empty(await CollectAsync(await StartedAsync(), Query.Global(text)));
    }

    [Fact]
    public async Task A_sum_it_will_not_answer_says_why_and_enter_does_nothing()
    {
        var result = Assert.Single(await CollectAsync(await StartedAsync(), Query.Global("1/0")));

        Assert.Equal("Cannot divide by zero.", result.Title);
        Assert.Equal(ActionOutcome.KeepOpen, await Run(result.Primary));
        Assert.Empty(_clipboard.Calls);
    }

    [Fact]
    public async Task After_equals_a_plain_number_is_answered()
    {
        Assert.Equal("2024", Assert.Single(await CollectAsync(await StartedAsync(), Scoped("=", "2024"))).Title);
    }

    [Fact]
    public async Task After_equals_what_is_not_a_sum_is_said_to_be_so()
    {
        var result = Assert.Single(await CollectAsync(await StartedAsync(), Scoped("=", "2 3")));
        Assert.Equal("Two numbers need an operator between them.", result.Title);
    }

    [Fact]
    public async Task Equals_alone_invites_a_sum()
    {
        var result = Assert.Single(await CollectAsync(await StartedAsync(), Scoped("=", "")));
        Assert.Equal("Type a sum", result.Title);
    }

    [Fact]
    public async Task A_rounded_answer_says_so()
    {
        var result = Assert.Single(await CollectAsync(await StartedAsync(), Query.Global("1/3")));
        Assert.EndsWith("(rounded)", result.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_it_answers_is_remembered()
    {
        Assert.False(typeof(IRecall).IsAssignableFrom(typeof(CalculatorProvider)));
    }
}
