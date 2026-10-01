using System.Globalization;

namespace Hail.Core.Settings;

/// <summary>
/// The modifier keys of a <see cref="Chord"/>. The values are Windows' own <c>MOD_*</c> bits, so
/// the chord reaches <c>RegisterHotKey</c> as it is.
/// </summary>
[Flags]
public enum ChordModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
}

/// <summary>
/// The system-wide shortcut that shows the box (Hail.md §8), as the settings file writes it
/// (<c>Alt+Space</c>, <c>Ctrl+Shift+K</c>) and as Windows registers it.
/// </summary>
/// <remarks>
/// Only keys in <see cref="Keys"/> are accepted: ones whose name is the same on every keyboard
/// layout. A punctuation key is a different character on another layout, so a shortcut written
/// with one would mean something else there, and the recorder could not say which key it is.
/// </remarks>
public sealed record Chord(ChordModifiers Modifiers, int VirtualKey)
{
    private const int VkSpace = 0x20;

    /// <summary>The default: Windows' own chord for a window's system menu, which registering it replaces everywhere.</summary>
    public static Chord AltSpace { get; } = new(ChordModifiers.Alt, VkSpace);

    /// <summary>Virtual-key codes and the names they are written with.</summary>
    private static readonly Dictionary<int, string> Keys = BuildKeys();

    private static readonly Dictionary<string, int> KeysByName = Keys.ToDictionary(k => k.Value, k => k.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>How the chord is written: <c>Ctrl+Alt+Shift+Win+K</c>, in Windows' own order.</summary>
    public string Display
    {
        get
        {
            var key = KeyName(VirtualKey) ?? string.Create(CultureInfo.InvariantCulture, $"0x{VirtualKey:X2}");
            return Modifiers == ChordModifiers.None ? key : $"{ModifiersDisplay(Modifiers)}+{key}";
        }
    }

    /// <summary>The modifiers alone, as a chord writes them: <c>Ctrl+Alt</c>; empty for none.</summary>
    public static string ModifiersDisplay(ChordModifiers modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ChordModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ChordModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ChordModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ChordModifiers.Windows))
        {
            parts.Add("Win");
        }

        return string.Join('+', parts);
    }

    /// <summary>True when <paramref name="virtualKey"/> can end a chord.</summary>
    public static bool IsChordKey(int virtualKey) => Keys.ContainsKey(virtualKey);

    /// <summary>The name a key is written with, or null for a key no chord may use.</summary>
    public static string? KeyName(int virtualKey) => Keys.GetValueOrDefault(virtualKey);

    /// <summary>
    /// Reads <c>Alt+Space</c> and its kind: modifiers and one key joined by <c>+</c>, in any
    /// order, any case, with spaces allowed around each part.
    /// </summary>
    /// <returns>The chord, or null with <paramref name="problem"/> saying why in a sentence.</returns>
    public static Chord? TryParse(string? text, out string problem)
    {
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64)
        {
            problem = "a shortcut is written like Alt+Space";
            return null;
        }

        var modifiers = ChordModifiers.None;
        int? key = null;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            var modifier = ModifierNamed(part);
            if (modifier != ChordModifiers.None)
            {
                if (modifiers.HasFlag(modifier))
                {
                    problem = $"\"{part}\" is written twice";
                    return null;
                }

                modifiers |= modifier;
                continue;
            }

            if (!KeysByName.TryGetValue(part, out var code))
            {
                problem = part.Length == 0 ? "a shortcut is written like Alt+Space" : $"\"{part}\" is not a key a shortcut can use";
                return null;
            }

            if (key is not null)
            {
                problem = "a shortcut has one key besides Ctrl, Alt, Shift and Win";
                return null;
            }

            key = code;
        }

        if (key is null)
        {
            problem = "a shortcut needs a key besides Ctrl, Alt, Shift and Win";
            return null;
        }

        var chord = new Chord(modifiers, key.Value);
        if (chord.Problem() is { } refused)
        {
            problem = refused;
            return null;
        }

        return chord;
    }

    /// <summary>
    /// Why Windows or the user would regret this chord, or null when it can be registered. Shift
    /// alone is refused because Shift+letter is typing; a chord must hold Ctrl, Alt or Win.
    /// </summary>
    public string? Problem()
    {
        if (!IsChordKey(VirtualKey))
        {
            return "that key cannot be part of a shortcut; use a letter, a digit, Space or a function key";
        }

        if ((Modifiers & (ChordModifiers.Control | ChordModifiers.Alt | ChordModifiers.Windows)) == ChordModifiers.None)
        {
            return "a shortcut needs Ctrl, Alt or Win, or it would take over ordinary typing";
        }

        // A system-wide shortcut takes its keys from every program. Ctrl or Alt alone with a
        // letter or digit is how programs name their own commands (Ctrl+C copies, Alt+F opens a
        // File menu), and Alt+F4, Ctrl+F4 and Ctrl+Alt+Delete close windows or belong to Windows.
        var letterOrDigit = VirtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9';
        if (letterOrDigit && Modifiers is ChordModifiers.Control or ChordModifiers.Alt)
        {
            return $"{Display} is a command in most programs; add Shift or Win, or use Space or a function key";
        }

        if (this == new Chord(ChordModifiers.Alt, 0x73) || this == new Chord(ChordModifiers.Control, 0x73)
            || (VirtualKey == 0x2E && Modifiers == (ChordModifiers.Control | ChordModifiers.Alt)))
        {
            return $"{Display} closes windows or belongs to Windows; choose another";
        }

        return null;
    }

    private static ChordModifiers ModifierNamed(string part) => part.ToUpperInvariant() switch
    {
        "ALT" => ChordModifiers.Alt,
        "CTRL" or "CONTROL" => ChordModifiers.Control,
        "SHIFT" => ChordModifiers.Shift,
        "WIN" or "WINDOWS" => ChordModifiers.Windows,
        _ => ChordModifiers.None,
    };

    private static Dictionary<int, string> BuildKeys()
    {
        var keys = new Dictionary<int, string>
        {
            [VkSpace] = "Space",
            [0x21] = "PageUp",
            [0x22] = "PageDown",
            [0x23] = "End",
            [0x24] = "Home",
            [0x25] = "Left",
            [0x26] = "Up",
            [0x27] = "Right",
            [0x28] = "Down",
            [0x2D] = "Insert",
            [0x2E] = "Delete",
        };

        for (var c = 'A'; c <= 'Z'; c++)
        {
            keys[c] = c.ToString();
        }

        for (var c = '0'; c <= '9'; c++)
        {
            keys[c] = c.ToString();
        }

        for (var f = 1; f <= 24; f++)
        {
            keys[0x70 + f - 1] = string.Create(CultureInfo.InvariantCulture, $"F{f}");
        }

        return keys;
    }
}
