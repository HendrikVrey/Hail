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
    private bool _keepLastQuery;
    private nint _handle;
    private bool _quitting;
    private bool _resetting;
    private bool _rewriting;

    public SearchWindow(SearchViewModel model, IHostLog log, bool keepLastQuery)
    {
        _model = model;
        _log = log;
        _keepLastQuery = keepLastQuery;
        DataContext = model;
        InitializeComponent();

        SourceInitialized += (_, _) => OnSourceInitialized();
        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Ctrl+, was pressed: the host opens Hail's settings.</summary>
    public event Action? SettingsRequested;

    public bool IsSummoned => IsVisible;

    /// <summary>The setting changed in the settings window; it counts from the next time the box hides.</summary>
    public void SetKeepLastQuery(bool keep) => _keepLastQuery = keep;

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

    /// <summary>
    /// Shows the box on the monitor under the mouse, focused. An empty box fills with the
    /// results picked most; a kept query is searched again and selected, so typing replaces it.
    /// </summary>
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
        SearchBox.SelectAll();

        _ = SearchAsync(SearchBox.Text);
    }

    /// <summary>Hides the box, clearing it for next time unless the setting keeps it (Hail.md §10.3).</summary>
    public void Dismiss()
    {
        if (!IsVisible)
        {
            return;
        }

        Hide();
        if (_keepLastQuery)
        {
            return;
        }

        // Emptying the field would search for the empty box; the summon does that when it is wanted.
        _resetting = true;
        try
        {
            SearchBox.Text = string.Empty;
        }
        finally
        {
            _resetting = false;
        }

        _model.Reset();
        UpdatePlaceholder();
    }

    /// <summary>Searches the box's text again, if the box is showing: the providers have changed under it.</summary>
    public void Refresh()
    {
        UpdatePlaceholder();
        if (IsVisible)
        {
            _ = SearchAsync(SearchBox.Text);
        }
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

        // The frame is extended under a transparent client area for the backdrop, so the
        // caption buttons Windows draws for a window with a system menu would show through it:
        // a close button in the corner of the box, red under the mouse. The box has no caption.
        WindowEffects.RemoveCaptionButtons(_handle);
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

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_rewriting)
        {
            OnTextEdited();
        }
    }

    private void OnTextEdited()
    {
        // "g " (or "=") finished at the start becomes the chip, and leaves the box with only
        // what follows it: the user sees the scope beside the field instead of a stray letter.
        if (!_resetting && _model.TryTakeChip(SearchBox.Text, out var rest))
        {
            Rewrite(rest);
        }

        UpdatePlaceholder();
        if (!_resetting)
        {
            _ = SearchAsync(SearchBox.Text);
        }
    }

    /// <summary>Puts <paramref name="text"/> in the field without it counting as typing; the caret goes to its end.</summary>
    private void Rewrite(string text)
    {
        _rewriting = true;
        try
        {
            SearchBox.Text = text;
            SearchBox.CaretIndex = text.Length;
        }
        finally
        {
            _rewriting = false;
        }
    }

    /// <summary>The hint shows only in an empty field with no chip; with a chip, the row under it says what to type.</summary>
    private void UpdatePlaceholder() =>
        Placeholder.Visibility = SearchBox.Text.Length == 0 && _model.Chip is null ? Visibility.Visible : Visibility.Hidden;

    /// <summary>Backspace at the very start of the field takes the chip away and searches the text everywhere.</summary>
    private bool TryRemoveChip()
    {
        if (SearchBox.CaretIndex != 0 || SearchBox.SelectionLength != 0 || !_model.RemoveChip())
        {
            return false;
        }

        UpdatePlaceholder();
        _ = SearchAsync(SearchBox.Text);
        return true;
    }

    private async Task SearchAsync(string text)
    {
        try
        {
            await _model.SearchAsync(text).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The end of the line for a keystroke; the failure is logged and the box lives.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.LogError($"A query of {text.Length} characters failed.", ex);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        var ctrl = modifiers == ModifierKeys.Control;
        var ctrlShift = modifiers == (ModifierKeys.Control | ModifierKeys.Shift);

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

            // A held Enter repeats; only the press itself acts, so holding it cannot run one
            // row after another or answer a question the first press asked.
            case Key.Enter:
                e.Handled = true;
                if (!e.IsRepeat && EnterGesture(modifiers) is { } gesture)
                {
                    _ = ExecuteAsync(gesture);
                }

                break;

            // Ctrl+C copies the box's own selected text, as in any text field; only with
            // nothing selected there does it go to the highlighted row.
            case Key.C when ctrl && SearchBox.SelectionLength == 0 && _model.Offers(Gesture.CtrlC):
                e.Handled = true;
                _ = ExecuteAsync(Gesture.CtrlC);
                break;

            case Key.C when ctrlShift && _model.Offers(Gesture.CtrlShiftC):
                e.Handled = true;
                _ = ExecuteAsync(Gesture.CtrlShiftC);
                break;

            // Tab completes; it never moves the focus out of the field.
            case Key.Tab:
                e.Handled = true;
                Complete();
                break;

            case Key.Back when modifiers == ModifierKeys.None && TryRemoveChip():
                e.Handled = true;
                break;

            case Key.OemComma when ctrl:
                e.Handled = true;
                Dismiss();
                SettingsRequested?.Invoke();
                break;

            case Key.Escape:
                e.Handled = true;
                if (!_model.CancelConfirmation())
                {
                    Dismiss();
                }

                break;
        }
    }

    private static Gesture? EnterGesture(ModifierKeys modifiers) => modifiers switch
    {
        ModifierKeys.None => Gesture.Enter,
        ModifierKeys.Control => Gesture.CtrlEnter,
        ModifierKeys.Shift => Gesture.ShiftEnter,
        ModifierKeys.Control | ModifierKeys.Shift => Gesture.CtrlShiftEnter,
        _ => null,
    };

    private void Complete()
    {
        if (_model.Completion is { } completion)
        {
            SetText(completion);
        }
    }

    /// <summary>
    /// Replaces the whole query (a completion, "keep calculating"): the text given is what would
    /// be typed, keyword and all, so the chip goes and is made again from it if it starts with one.
    /// </summary>
    private void SetText(string text)
    {
        _model.RemoveChip();
        if (SearchBox.Text == text)
        {
            // No change, so no TextChanged: the chip is made again and the search run here.
            OnTextEdited();
        }
        else
        {
            SearchBox.Text = text;
        }

        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private async Task ExecuteAsync(Gesture gesture)
    {
        try
        {
            var outcome = await _model.ExecuteAsync(gesture).ConfigureAwait(true);
            switch (outcome)
            {
                case ActionOutcome.HideBox:
                    Dismiss();
                    break;
                case ActionOutcome.ReplaceQueryText replace:
                    SetText(replace.Text);
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
            _ = ExecuteAsync(Gesture.Enter);
        }
    }
}
