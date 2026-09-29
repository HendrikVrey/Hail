using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Hail.Sdk;

namespace Hail.App;

/// <summary>One row as the box draws it.</summary>
internal sealed class ResultRow(Result result) : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Result Result { get; } = result;

    public string Title => Result.Title;

    public string? Subtitle => Result.Subtitle;

    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Result.Subtitle);

    public MatchSpans? Highlight => Result.Highlight;

    public string ActionTitle => Result.Primary.Title;

    public string ActionChord => Chord(Result.Primary.Gesture);

    public ImageSource? Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>How a gesture is written on a keycap.</summary>
    public static string Chord(Gesture gesture) => gesture switch
    {
        Gesture.Enter => "Enter",
        Gesture.CtrlEnter => "Ctrl+Enter",
        Gesture.ShiftEnter => "Shift+Enter",
        Gesture.CtrlShiftEnter => "Ctrl+Shift+Enter",
        Gesture.CtrlC => "Ctrl+C",
        _ => gesture.ToString(),
    };

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
