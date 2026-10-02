using Hail.Windows.Interop;

namespace Hail.Windows.Windowing;

/// <summary>
/// What the box asks of the desktop window manager and the window manager: the transient
/// acrylic backdrop, Windows 11's corners, the dark or light frame, staying out of Alt+Tab,
/// and taking the foreground (Hail.md §10.2).
/// </summary>
public static class WindowEffects
{
    /// <summary>
    /// Keeps a window out of the taskbar and Alt+Tab. The box is summoned by a chord, not
    /// switched to.
    /// </summary>
    public static void MakeToolWindow(nint window)
    {
        var style = User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE);
        style = (style | User32.WS_EX_TOOLWINDOW) & ~User32.WS_EX_APPWINDOW;
        User32.SetWindowLongPtr(window, User32.GWL_EXSTYLE, style);
    }

    /// <summary>
    /// Takes the system menu, and with it the close, minimise and maximise buttons, off a window
    /// that draws no caption of its own. A window whose frame is extended under a transparent
    /// client area otherwise shows those buttons through it.
    /// </summary>
    public static void RemoveCaptionButtons(nint window)
    {
        var style = User32.GetWindowLongPtr(window, User32.GWL_STYLE);
        style &= ~(User32.WS_SYSMENU | User32.WS_MINIMIZEBOX | User32.WS_MAXIMIZEBOX);
        User32.SetWindowLongPtr(window, User32.GWL_STYLE, style);

        // A changed style takes effect on the frame only when Windows is told the frame changed.
        _ = User32.SetWindowPos(
            window,
            0,
            0,
            0,
            0,
            0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Asks for the acrylic Windows 11 gives transient surfaces, across the whole window.
    /// False where the system has no backdrops (Windows 10, and Windows 11 before 22H2), and
    /// the caller then paints a solid background of its own.
    /// </summary>
    public static bool TryApplyTransientBackdrop(nint window)
    {
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (DwmApi.DwmExtendFrameIntoClientArea(window, ref margins) != 0)
        {
            return false;
        }

        var corners = DwmApi.DWMWCP_ROUND;
        _ = DwmApi.DwmSetWindowAttribute(window, DwmApi.DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));

        var backdrop = DwmApi.DWMSBT_TRANSIENTWINDOW;
        return DwmApi.DwmSetWindowAttribute(window, DwmApi.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;
    }

    /// <summary>Tints the frame and the backdrop dark or light, to match the app's theme.</summary>
    public static void SetDarkFrame(nint window, bool dark)
    {
        var value = dark ? 1 : 0;
        _ = DwmApi.DwmSetWindowAttribute(window, DwmApi.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    /// <summary>
    /// Brings a window to the front. Succeeds when Hail may take the foreground, which it may
    /// while handling its hotkey: WM_HOTKEY grants that right, and it is why a launcher
    /// summoned this way can be relied on to receive the keystrokes that follow.
    /// </summary>
    public static bool BringToFront(nint window) => User32.SetForegroundWindow(window);
}
