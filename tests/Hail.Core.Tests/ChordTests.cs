using Hail.Core.Settings;

namespace Hail.Core.Tests;

/// <summary>The shortcut as the settings file writes it and Windows registers it.</summary>
public sealed class ChordTests
{
    [Theory]
    [InlineData("Alt+Space", ChordModifiers.Alt, 0x20)]
    [InlineData("alt + space", ChordModifiers.Alt, 0x20)]
    [InlineData("Ctrl+Shift+K", ChordModifiers.Control | ChordModifiers.Shift, 0x4B)]
    [InlineData("Control+Alt+F12", ChordModifiers.Control | ChordModifiers.Alt, 0x7B)]
    [InlineData("Win+Alt+Space", ChordModifiers.Windows | ChordModifiers.Alt, 0x20)]
    [InlineData("Space+Alt", ChordModifiers.Alt, 0x20)]
    [InlineData("Ctrl+Alt+7", ChordModifiers.Control | ChordModifiers.Alt, 0x37)]
    [InlineData("Win+PageUp", ChordModifiers.Windows, 0x21)]
    public void A_written_chord_is_read(string text, ChordModifiers modifiers, int key)
    {
        var chord = Chord.TryParse(text, out var problem);

        Assert.Equal(string.Empty, problem);
        Assert.Equal(new Chord(modifiers, key), chord);
    }

    [Theory]
    [InlineData("Alt+Space")]
    [InlineData("Ctrl+Alt+Shift+Win+K")]
    [InlineData("Ctrl+F24")]
    [InlineData("Win+Delete")]
    public void Display_is_what_is_read_back(string text)
    {
        var chord = Chord.TryParse(text, out _)!;

        Assert.Equal(text, chord.Display);
        Assert.Equal(chord, Chord.TryParse(chord.Display, out _));
    }

    [Fact]
    public void Modifiers_are_written_in_windows_order_whatever_order_they_were_typed() =>
        Assert.Equal("Ctrl+Alt+Shift+Win+Q", Chord.TryParse("Win+Shift+Q+Alt+Ctrl", out _)!.Display);

    [Theory]
    [InlineData(null, "written like Alt+Space")]
    [InlineData("", "written like Alt+Space")]
    [InlineData("Alt+", "written like Alt+Space")]
    [InlineData("Alt", "needs a key")]
    [InlineData("Alt+Alt+Space", "written twice")]
    [InlineData("Alt+A+B", "one key")]
    [InlineData("Alt+;", "not a key")]
    [InlineData("Alt+Enter", "not a key")]
    [InlineData("Space", "Ctrl, Alt or Win")]
    [InlineData("Shift+A", "Ctrl, Alt or Win")]
    [InlineData("F5", "Ctrl, Alt or Win")]
    public void What_cannot_be_a_shortcut_is_refused_in_a_sentence(string? text, string said)
    {
        Assert.Null(Chord.TryParse(text, out var problem));
        Assert.Contains(said, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ctrl+C")]
    [InlineData("Ctrl+V")]
    [InlineData("Ctrl+A")]
    [InlineData("Ctrl+7")]
    [InlineData("Alt+F")]
    [InlineData("Alt+F4")]
    [InlineData("Ctrl+F4")]
    [InlineData("Ctrl+Alt+Delete")]
    public void A_chord_that_would_take_an_everyday_command_from_every_program_is_refused(string text)
    {
        // The review's scenario: Change, then Ctrl+A in a text box, made Ctrl+A Hail's everywhere.
        Assert.Null(Chord.TryParse(text, out var problem));
        Assert.Contains(text, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ctrl+Shift+C")]
    [InlineData("Win+C")]
    [InlineData("Ctrl+Space")]
    [InlineData("Alt+F5")]
    public void The_same_keys_with_more_held_are_allowed(string text) =>
        Assert.NotNull(Chord.TryParse(text, out _));

    [Fact]
    public void A_very_long_text_is_refused_without_being_read() =>
        Assert.Null(Chord.TryParse(string.Join('+', Enumerable.Repeat("Alt", 40)) + "+Space", out _));

    [Fact]
    public void The_default_is_alt_space() =>
        Assert.Equal("Alt+Space", Chord.AltSpace.Display);

    [Fact]
    public void A_key_outside_the_table_is_a_problem_even_when_built_by_hand()
    {
        Assert.NotNull(new Chord(ChordModifiers.Alt, 0xBA).Problem());
        Assert.Null(new Chord(ChordModifiers.Alt | ChordModifiers.Shift, 0x41).Problem());
    }

    [Fact]
    public void The_modifier_bits_are_windows_own() =>
        Assert.Equal(
            (0x0001, 0x0002, 0x0004, 0x0008),
            ((int)ChordModifiers.Alt, (int)ChordModifiers.Control, (int)ChordModifiers.Shift, (int)ChordModifiers.Windows));
}
