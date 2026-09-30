using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Hail.Sdk;

namespace Hail.App;

/// <summary>An action and its chord, as the footer writes it.</summary>
internal sealed record ActionHint(string Title, string Chord);

/// <summary>One row as the box draws it.</summary>
internal sealed class ResultRow(Result result) : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _isSelected;
    private bool _iconMissing;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Result Result { get; } = result;

    public string Title => Result.Title;

    public string? Subtitle => Result.Subtitle;

    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Result.Subtitle);

    public MatchSpans? Highlight => Result.Highlight;

    public string ActionTitle => Result.Primary.Title;

    public string ActionChord => Chord(Result.Primary.Gesture);

    /// <summary>The icon font's character, when the provider asked for a glyph rather than a shell icon.</summary>
    public string? Glyph => (Result.Icon as IconSource.Glyph)?.Character;

    public bool HasGlyph => Glyph is not null;

    /// <summary>The other things the row can do, for the footer while it is highlighted.</summary>
    public IReadOnlyList<ActionHint> Secondary { get; } =
        [.. result.Secondary.Select(a => new ActionHint(a.Title, Chord(a.Gesture)))];

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

    /// <summary>The shell had no icon for it; the row shows a plain document glyph instead of a gap.</summary>
    public bool IconMissing
    {
        get => _iconMissing;
        set => Set(ref _iconMissing, value);
    }

    /// <summary>How a gesture is written on a keycap.</summary>
    public static string Chord(Gesture gesture) => gesture switch
    {
        Gesture.Enter => "Enter",
        Gesture.CtrlEnter => "Ctrl+Enter",
        Gesture.ShiftEnter => "Shift+Enter",
        Gesture.CtrlShiftEnter => "Ctrl+Shift+Enter",
        Gesture.CtrlC => "Ctrl+C",
        Gesture.CtrlShiftC => "Ctrl+Shift+C",
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
