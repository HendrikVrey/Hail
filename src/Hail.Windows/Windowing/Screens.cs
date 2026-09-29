using System.ComponentModel;
using System.Runtime.InteropServices;
using Hail.Core.Layout;
using Hail.Windows.Interop;

namespace Hail.Windows.Windowing;

/// <summary>A monitor's work area in physical pixels, and its scale (1.0 at 96 DPI).</summary>
public sealed record MonitorArea(PixelRect WorkArea, double Scale);

/// <summary>Monitors and window positions, in physical pixels throughout.</summary>
public static class Screens
{
    /// <summary>The monitor the mouse is on, which is where the box appears (Hail.md §10.1).</summary>
    public static MonitorArea UnderCursor()
    {
        if (!User32.GetCursorPos(out var cursor))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var monitor = User32.MonitorFromPoint(cursor, User32.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!User32.GetMonitorInfo(monitor, ref info))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var scale = ShCore.GetDpiForMonitor(monitor, ShCore.MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 && dpi > 0
            ? dpi / 96.0
            : User32.GetDpiForSystem() / 96.0;

        return new MonitorArea(ToPixelRect(info.Work), scale);
    }

    public static PixelRect WindowBounds(nint window)
    {
        if (!User32.GetWindowRect(window, out var rect))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return ToPixelRect(rect);
    }

    /// <summary>Moves a window's top-left corner without resizing, activating or restacking it.</summary>
    public static void MoveTo(nint window, int x, int y)
    {
        if (!User32.SetWindowPos(window, 0, x, y, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static PixelRect ToPixelRect(RECT rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);
}
