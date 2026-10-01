using Hail.Windows.Interop;

namespace Hail.Windows.Tray;

/// <summary>What happened to the tray icon.</summary>
public enum TrayEvent
{
    None,

    /// <summary>Clicked, or chosen with the keyboard.</summary>
    Select,

    /// <summary>Right-clicked, or Shift+F10 / the menu key.</summary>
    ContextMenu,

    /// <summary>The last notification was clicked.</summary>
    NotificationClicked,
}

/// <summary>
/// Hail's icon in the notification area (Hail.md §5): where Quit lives, and where Hail says
/// its hotkey could not be registered.
/// </summary>
/// <remarks>
/// <para>
/// Events arrive as <see cref="CallbackMessage"/> on the owning window, which must be a
/// top-level window (hidden is fine): a message-only window never hears
/// <see cref="TaskbarCreatedMessage"/>, and without it the icon is lost for good when
/// Explorer restarts. Pass that message to <see cref="Restore"/>.
/// </para>
/// <para>
/// Uses NOTIFYICON_VERSION_4, so a click is one event (NIN_SELECT) rather than a button
/// down and up to pair.
/// </para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    /// <summary>WM_APP + 1.</summary>
    public const int CallbackMessage = 0x8001;

    private const uint IconId = 1;
    private const int NinSelect = 0x0400;
    private const int NinKeySelect = 0x0401;
    private const int NinBalloonUserClick = 0x0405;
    private const int WmContextMenu = 0x007B;

    /// <summary>The resource id the .NET SDK gives an ApplicationIcon (IDI_APPLICATION).</summary>
    private const nint ApplicationIconResource = 32512;

    private readonly nint _window;
    private string _tip;
    private nint _icon;
    private bool _added;

    public TrayIcon(nint window, string tip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tip);
        _window = window;
        _tip = tip;
        _icon = LoadApplicationIcon();
        Add();
    }

    /// <summary>Broadcast by Explorer when the taskbar is created, including after a crash.</summary>
    public static int TaskbarCreatedMessage { get; } = (int)User32.RegisterWindowMessage("TaskbarCreated");

    /// <summary>Reads a <see cref="CallbackMessage"/>'s lParam.</summary>
    public static TrayEvent Interpret(nint lParam) => (int)(lParam & 0xFFFF) switch
    {
        NinSelect or NinKeySelect => TrayEvent.Select,
        WmContextMenu => TrayEvent.ContextMenu,
        NinBalloonUserClick => TrayEvent.NotificationClicked,
        _ => TrayEvent.None,
    };

    /// <summary>Puts the icon back after Explorer has restarted.</summary>
    public void Restore()
    {
        _added = false;
        Add();
    }

    /// <summary>Changes the text shown when the pointer rests on the icon (it names the shortcut).</summary>
    public unsafe void SetTip(string tip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tip);
        _tip = tip;
        var data = NewData(Shell32.NIF_TIP | Shell32.NIF_SHOWTIP);
        Copy(_tip, data.Tip, 128);
        Shell32.Shell_NotifyIcon(Shell32.NIM_MODIFY, ref data);
    }

    /// <summary>
    /// A notification from the icon, for things the user needs to know once: a warning (the
    /// hotkey is taken), or news (a plugin is waiting for an answer).
    /// </summary>
    public unsafe void Notify(string title, string text, bool warning = true)
    {
        var data = NewData(Shell32.NIF_INFO);
        Copy(title, data.InfoTitle, 64);
        Copy(text, data.Info, 256);
        data.InfoFlags = warning ? Shell32.NIIF_WARNING : Shell32.NIIF_INFO;
        Shell32.Shell_NotifyIcon(Shell32.NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData(0);
            Shell32.Shell_NotifyIcon(Shell32.NIM_DELETE, ref data);
            _added = false;
        }

        if (_icon != 0)
        {
            User32.DestroyIcon(_icon);
            _icon = 0;
        }
    }

    private unsafe void Add()
    {
        var data = NewData(Shell32.NIF_MESSAGE | Shell32.NIF_ICON | Shell32.NIF_TIP | Shell32.NIF_SHOWTIP);
        data.CallbackMessage = CallbackMessage;
        data.Icon = _icon;
        Copy(_tip, data.Tip, 128);

        if (Shell32.Shell_NotifyIcon(Shell32.NIM_ADD, ref data))
        {
            data.TimeoutOrVersion = Shell32.NOTIFYICON_VERSION_4;
            Shell32.Shell_NotifyIcon(Shell32.NIM_SETVERSION, ref data);
            _added = true;
        }
    }

    private unsafe NOTIFYICONDATAW NewData(uint flags) => new()
    {
        Size = sizeof(NOTIFYICONDATAW),
        Window = _window,
        Id = IconId,
        Flags = flags,
    };

    /// <summary>Copies text into a fixed buffer, truncated to leave room for the terminator.</summary>
    private static unsafe void Copy(string text, char* buffer, int capacity)
    {
        var length = Math.Min(text.Length, capacity - 1);
        for (var i = 0; i < length; i++)
        {
            buffer[i] = text[i];
        }

        buffer[length] = '\0';
    }

    /// <summary>The executable's own icon, at the small-icon size for the system DPI.</summary>
    private static nint LoadApplicationIcon()
    {
        var dpi = User32.GetDpiForSystem();
        var width = User32.GetSystemMetricsForDpi(User32.SM_CXSMICON, dpi);
        var height = User32.GetSystemMetricsForDpi(User32.SM_CYSMICON, dpi);
        return User32.LoadImage(Kernel32.GetModuleHandle(0), ApplicationIconResource, User32.IMAGE_ICON, width, height, 0);
    }
}
