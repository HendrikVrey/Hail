using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Hail.App;

/// <summary>
/// Draws a row's title with the characters that matched in bold, so the user can see why a
/// row is there.
/// </summary>
/// <remarks>
/// The spans come from a provider, which is code the host did not write: they are clamped to
/// the title here rather than trusted, so a span past the end draws nothing instead of
/// throwing inside a layout pass.
/// </remarks>
internal static class HighlightedTitle
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.RegisterAttached(
        "Row",
        typeof(ResultRow),
        typeof(HighlightedTitle),
        new PropertyMetadata(null, OnRowChanged));

    public static ResultRow? GetRow(DependencyObject element) => (ResultRow?)element.GetValue(RowProperty);

    public static void SetRow(DependencyObject element, ResultRow? value) => element.SetValue(RowProperty, value);

    private static void OnRowChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        if (e.NewValue is not ResultRow row)
        {
            return;
        }

        var title = row.Title;
        var cursor = 0;
        foreach (var span in row.Highlight?.Spans ?? [])
        {
            var start = Math.Clamp(span.Start, cursor, title.Length);
            var end = Math.Clamp(span.End, start, title.Length);
            if (start > cursor)
            {
                block.Inlines.Add(new Run(title[cursor..start]));
            }

            if (end > start)
            {
                block.Inlines.Add(new Run(title[start..end]) { FontWeight = FontWeights.Bold });
            }

            cursor = end;
        }

        if (cursor < title.Length)
        {
            block.Inlines.Add(new Run(title[cursor..]));
        }
    }
}
