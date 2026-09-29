using System.Runtime.InteropServices;

namespace Hail.Windows.Windowing;

/// <summary>Reads the few window messages the app reacts to, so their numbers live here.</summary>
public static class SystemMessages
{
    private const int WmSettingChange = 0x001A;
    private const int WmSysCommand = 0x0112;
    private const int ScKeyMenu = 0xF100;

    /// <summary>Windows switched between light and dark, or changed its accent.</summary>
    public static bool IsThemeChange(int message, nint lParam) =>
        message == WmSettingChange
        && lParam != 0
        && string.Equals(Marshal.PtrToStringUni(lParam), "ImmersiveColorSet", StringComparison.Ordinal);

    /// <summary>
    /// The request a lone Alt release turns into: open the window's system menu. The box
    /// swallows it, because the Alt of Alt+Space is released after the box has the focus
    /// (Hail.md §8), and it must stay in the search field.
    /// </summary>
    public static bool IsKeyMenu(int message, nint wParam) =>
        message == WmSysCommand && ((int)wParam & 0xFFF0) == ScKeyMenu;
}
