namespace Hail.Core.Layout;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>Where the box goes on a monitor (Hail.md §10.1).</summary>
public static class BoxPlacement
{
    /// <summary>
    /// The box's top-left corner: centred across the monitor's work area and a third of the
    /// way down it. Only the top is fixed, so the box grows downward as rows arrive and the
    /// field the user is typing into never moves.
    /// </summary>
    /// <remarks>
    /// Clamped to the work area, so a box wider than a small monitor starts at its left edge
    /// rather than off it.
    /// </remarks>
    public static (int X, int Y) TopLeft(PixelRect workArea, int boxWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(boxWidth);

        var x = workArea.Left + ((workArea.Width - boxWidth) / 2);
        var y = workArea.Top + (workArea.Height / 3);
        return (Math.Max(x, workArea.Left), y);
    }
}
