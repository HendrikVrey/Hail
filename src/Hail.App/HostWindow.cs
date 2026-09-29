using System.Windows.Interop;
using Hail.Windows.Hotkeys;
using Hail.Windows.Tray;
using Hail.Windows.Windowing;

namespace Hail.App;

/// <summary>
/// A hidden top-level window that receives what Hail listens for when the box is not showing:
/// the hotkey, the tray icon's clicks, Explorer restarting, and Windows changing theme.
/// </summary>
/// <remarks>
/// Top-level and hidden rather than message-only, because a message-only window never
/// receives broadcasts, and both "the taskbar was re-created" and "the theme changed" arrive
/// as broadcasts.
/// </remarks>
internal sealed class HostWindow : IDisposable
{
    public const int HotkeyId = 1;

    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;

    private readonly HwndSource _source;

    public HostWindow()
    {
        var parameters = new HwndSourceParameters("Hail")
        {
            WindowStyle = WsPopup,
            ExtendedWindowStyle = WsExToolWindow,
            Width = 0,
            Height = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(Hook);
    }

    public event Action? HotkeyPressed;

    public event Action<TrayEvent>? TrayActivated;

    public event Action? TaskbarCreated;

    public event Action? ThemeChanged;

    public nint Handle => _source.Handle;

    public void Dispose()
    {
        _source.RemoveHook(Hook);
        _source.Dispose();
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == GlobalHotkey.WmHotkey && wParam == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        else if (message == TrayIcon.CallbackMessage)
        {
            var trayEvent = TrayIcon.Interpret(lParam);
            if (trayEvent != TrayEvent.None)
            {
                TrayActivated?.Invoke(trayEvent);
            }

            handled = true;
        }
        else if (message == TrayIcon.TaskbarCreatedMessage)
        {
            TaskbarCreated?.Invoke();
        }
        else if (SystemMessages.IsThemeChange(message, lParam))
        {
            ThemeChanged?.Invoke();
        }

        return 0;
    }
}
