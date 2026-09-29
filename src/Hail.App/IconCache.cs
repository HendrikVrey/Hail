using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hail.Sdk;
using Hail.Windows.Icons;

namespace Hail.App;

/// <summary>
/// Icons for rows, rendered by the shell on the STA worker and kept by parsing name and pixel
/// size (Hail.md §7.1). Used from the UI thread only.
/// </summary>
internal sealed class IconCache(ShellIcons icons)
{
    /// <summary>
    /// Well above a Start menu's worth at one scale. Past it the cache starts again rather
    /// than growing, which costs a few re-renders once in a long while.
    /// </summary>
    private const int Capacity = 1024;

    private readonly Dictionary<(string ParsingName, int Size), ImageSource?> _images = [];

    public async Task<ImageSource?> GetAsync(IconSource source, int size, CancellationToken ct)
    {
        if (source is not IconSource.ShellItem shellItem)
        {
            return null;
        }

        var clamped = Math.Clamp(size, 16, ShellIcons.MaxSize);
        var key = (shellItem.ParsingName, clamped);
        if (_images.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var pixels = await icons.LoadAsync(shellItem.ParsingName, clamped, ct).ConfigureAwait(true);
        var image = pixels is null ? null : ToImage(pixels);

        if (_images.Count >= Capacity)
        {
            _images.Clear();
        }

        _images[key] = image;
        return image;
    }

    private static BitmapSource ToImage(IconPixels pixels)
    {
        var format = pixels.Premultiplied ? PixelFormats.Pbgra32 : PixelFormats.Bgra32;
        var image = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, format, null, pixels.Bgra, pixels.Stride);
        image.Freeze();
        return image;
    }
}
