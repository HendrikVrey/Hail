using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Hail.Core.Layout;
using Hail.Core.Ports;
using Hail.Sdk;
using Hail.Windows.Windowing;
using Wpf.Ui.Appearance;

namespace Hail.App;

/// <summary>
/// The box. Created once at startup, laid out once, and hidden; the hotkey only moves it to
/// the right monitor, shows it and focuses the field (Hail.md §3.1, §5). It is never closed
/// until Hail quits.
/// </summary>
internal sealed partial class SearchWindow : Window
{
    private readonly SearchViewModel _model;
    private readonly IHostLog _log;
    private nint _handle;
    private bool _quitting;

    public SearchWindow(SearchViewModel model, IHostLog log)
    {
        _model = model;
        _log = log;
        DataContext = model;
        InitializeComponent();

        SourceInitialized += (_, _) => OnSourceInitialized();
        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public bool IsSummoned => IsVisible;

    /// <summary>
    /// Creates the window's handle and pays for its first layout and render now, off screen
    /// and without activation, so the first summon costs no more than every other one.
    /// </summary>
    public void Prepare()
    {
        new WindowInteropHelper(this).EnsureHandle();

        ShowActivated = false;
        Show();
        Hide();
        ShowActivated = true;
    }

    /// <summary>Shows the box on the monitor under the mouse, focused and empty.</summary>
    public void Summon()
    {
        var monitor = Screens.UnderCursor();
        _model.Scale = monitor.Scale;

        // Moving onto a monitor at another scale makes WPF resize the window as it arrives, so
        // the width is read back and the box centred again once it is there.
        PlaceOn(monitor, (int)Math.Round(ActualWidth * monitor.Scale));
        PlaceOn(monitor, Screens.WindowBounds(_handle).Width);

        Show();
        Activate();
        if (!WindowEffects.BringToFront(_handle))
        {
            _log.LogInfo("Windows refused Hail the foreground on a summon; the box may not have the keyboard.");
        }

        PlaceOn(monitor, Screens.WindowBounds(_handle).Width);
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    /// <summary>Hides the box and clears it for next time (Hail.md §10.3, Escape).</summary>
    public void Dismiss()
    {
        if (!IsVisible)
        {
            return;
        }

        Hide();
        SearchBox.Text = string.Empty;
        _model.Reset();
    }

    /// <summary>Lets the window close for real; Hail is quitting.</summary>
    public void CloseForQuit()
    {
        _quitting = true;
        Close();
    }

    /// <summary>Follows Windows between light and dark, frame and backdrop included.</summary>
    public void ApplyTheme()
    {
        if (_handle != 0)
        {
            WindowEffects.SetDarkFrame(_handle, ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 would destroy the window the hotkey shows; it hides the box instead.
        if (!_quitting)
        {
            e.Cancel = true;
            Dismiss();
        }

        base.OnClosing(e);
    }

    private void OnSourceInitialized()
    {
        _handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_handle);
        source.AddHook(Hook);

        WindowEffects.MakeToolWindow(_handle);
        ApplyTheme();

        if (WindowEffects.TryApplyTransientBackdrop(_handle))
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
        else
        {
            // No system backdrop (Windows 10, or 11 before 22H2): a solid surface instead.
            Surface.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
            _log.LogInfo("The transient backdrop is not available here; the box is drawn solid.");
        }
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (SystemMessages.IsKeyMenu(message, wParam))
        {
            handled = true;
        }

        return 0;
    }

    private void PlaceOn(MonitorArea monitor, int widthInPixels)
    {
        var (x, y) = BoxPlacement.TopLeft(monitor.WorkArea, widthInPixels);
        Screens.MoveTo(_handle, x, y);
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        try
        {
            await _model.SearchAsync(SearchBox.Text).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // An event handler is the end of the line; the failure is logged and the box lives.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.LogError($"A query of {SearchBox.Text.Length} characters failed.", ex);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        switch (e.Key)
        {
            case Key.Down:
            case Key.J when ctrl:
                _model.MoveDown();
                e.Handled = true;
                break;
            case Key.Up:
            case Key.K when ctrl:
                _model.MoveUp();
                e.Handled = true;
                break;
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                _ = ExecuteAsync();
                break;
            case Key.Escape:
                e.Handled = true;
                Dismiss();
                break;
        }
    }

    private async Task ExecuteAsync()
    {
        try
        {
            var outcome = await _model.ExecuteSelectedAsync().ConfigureAwait(true);
            switch (outcome)
            {
                case ActionOutcome.HideBox:
                    Dismiss();
                    break;
                case ActionOutcome.ReplaceQueryText replace:
                    SearchBox.Text = replace.Text;
                    SearchBox.CaretIndex = SearchBox.Text.Length;
                    break;
            }
        }
#pragma warning disable CA1031 // As above: logged, and the box stays usable.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.LogError("Running a result's action failed.", ex);
        }
    }

    // A row follows the mouse only when the mouse moves, never when rows arrive under a
    // pointer that is standing still (Hail.md §3.2).
    private void Row_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ResultRow row } && !row.IsSelected)
        {
            _model.Select(row);
        }
    }

    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ResultRow row })
        {
            _model.Select(row);
            _ = ExecuteAsync();
        }
    }
}
