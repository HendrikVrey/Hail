using System.Runtime.InteropServices;
using Hail.Windows.Interop;

namespace Hail.Windows.Hotkeys;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
}

/// <summary>A system-wide chord, and how the user would write it.</summary>
public sealed record HotkeyChord(HotkeyModifiers Modifiers, uint VirtualKey, string Display)
{
    private const uint VkSpace = 0x20;

    /// <summary>
    /// The default (Hail.md §8): Windows' own chord for a window's system menu, which
    /// registering it replaces everywhere, as PowerToys Run did.
    /// </summary>
    public static HotkeyChord AltSpace { get; } = new(HotkeyModifiers.Alt, VkSpace, "Alt+Space");
}

/// <summary>
/// Registers a chord with Windows, delivered as WM_HOTKEY to a window of Hail's own.
/// </summary>
public static class GlobalHotkey
{
    public const int WmHotkey = User32.WM_HOTKEY;

    /// <summary>Another program holds the chord (PowerToys Run is the usual one for Alt+Space).</summary>
    public const int ErrorAlreadyRegistered = 1409;

    /// <summary>MOD_NOREPEAT: holding the chord down summons once, not at the key-repeat rate.</summary>
    private const uint NoRepeat = 0x4000;

    /// <summary>Zero when registered; otherwise the Win32 error that refused it.</summary>
    public static int Register(nint window, int id, HotkeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        return User32.RegisterHotKey(window, id, (uint)chord.Modifiers | NoRepeat, chord.VirtualKey)
            ? 0
            : Marshal.GetLastPInvokeError();
    }

    public static void Unregister(nint window, int id) => User32.UnregisterHotKey(window, id);
}
