using System.Runtime.InteropServices;
using Hail.Core.Settings;
using Hail.Windows.Interop;

namespace Hail.Windows.Hotkeys;

/// <summary>
/// Registers a chord with Windows, delivered as WM_HOTKEY to a window of Hail's own. The chord's
/// modifier bits are Windows' own <c>MOD_*</c> values (<see cref="ChordModifiers"/>).
/// </summary>
public static class GlobalHotkey
{
    public const int WmHotkey = User32.WM_HOTKEY;

    /// <summary>Another program holds the chord (PowerToys Run is the usual one for Alt+Space).</summary>
    public const int ErrorAlreadyRegistered = 1409;

    /// <summary>MOD_NOREPEAT: holding the chord down summons once, not at the key-repeat rate.</summary>
    private const uint NoRepeat = 0x4000;

    /// <summary>Zero when registered; otherwise the Win32 error that refused it.</summary>
    public static int Register(nint window, int id, Chord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        return User32.RegisterHotKey(window, id, (uint)chord.Modifiers | NoRepeat, (uint)chord.VirtualKey)
            ? 0
            : Marshal.GetLastPInvokeError();
    }

    public static void Unregister(nint window, int id) => User32.UnregisterHotKey(window, id);

    /// <summary>What to tell the user when <see cref="Register"/> answered <paramref name="error"/>.</summary>
    public static string Describe(Chord chord, int error)
    {
        ArgumentNullException.ThrowIfNull(chord);
        return error == ErrorAlreadyRegistered
            ? $"Another program already uses {chord.Display}{(chord == Chord.AltSpace ? " (PowerToys Run often does)" : string.Empty)}."
            : $"Windows would not give Hail {chord.Display} (error {error}).";
    }
}
