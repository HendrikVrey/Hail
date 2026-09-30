using System.Runtime.InteropServices;
using Hail.Core.Ports;
using Hail.Windows.Interop;
using Hail.Windows.Launching;

namespace Hail.Windows.Icons;

/// <summary>
/// An icon as pixels: 32-bit BGRA, rows top to bottom, no padding.
/// </summary>
/// <param name="Premultiplied">
/// True when the colour channels are already multiplied by alpha, which is what the shell
/// returns for an icon with transparency.
/// </param>
public sealed record IconPixels(int Width, int Height, byte[] Bgra, bool Premultiplied)
{
    public int Stride => Width * 4;
}

/// <summary>
/// The shell's own icon for an item, rendered at an exact pixel size (Hail.md §7.1). Runs on
/// the STA worker, never on the UI thread.
/// </summary>
public sealed class ShellIcons(StaWorker worker)
{
    /// <summary>Largest icon anyone asks for; a 32 DIP row at 400 % scaling is 128.</summary>
    public const int MaxSize = 256;

    /// <summary>
    /// Null when the shell has no icon for <paramref name="parsingName"/>. A parsing name the
    /// shell cannot resolve at all is also null: a missing icon is not worth an error.
    /// </summary>
    public Task<IconPixels?> LoadAsync(string parsingName, int size, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parsingName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, MaxSize);

        // An icon source is a provider's data: resolving \\server\share\x would reach that
        // server with the user's credentials, which the launcher's rules forbid, so the shell
        // is only asked about the Start menu's apps and local paths.
        if (!parsingName.StartsWith(AppsFolder.Prefix, StringComparison.OrdinalIgnoreCase) && !LocalPaths.IsAcceptable(parsingName))
        {
            return Task.FromResult<IconPixels?>(null);
        }

        return worker.RunAsync(() => Load(parsingName, size), ct);
    }

    private static IconPixels? Load(string parsingName, int size)
    {
        IShellItemImageFactory factory;
        try
        {
            factory = (IShellItemImageFactory)ShellNative.SHCreateItemFromParsingName(
                parsingName, 0, ShellNative.IidShellItemImageFactory);
        }
        catch (COMException)
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        try
        {
            var requested = new SIZE { Width = size, Height = size };
            if (factory.GetImage(requested, ShellNative.SIIGBF_ICONONLY, out var bitmap) != 0 || bitmap == 0)
            {
                return null;
            }

            try
            {
                return ReadBitmap(bitmap);
            }
            finally
            {
                Gdi32.DeleteObject(bitmap);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(factory);
        }
    }

    private static unsafe IconPixels? ReadBitmap(nint bitmap)
    {
        if (Gdi32.GetObject(bitmap, sizeof(BITMAP), out var info) == 0 || info.Width <= 0 || info.Height <= 0)
        {
            return null;
        }

        var width = info.Width;
        var height = info.Height;
        var pixels = new byte[width * height * 4];

        var header = new BITMAPINFO
        {
            Header = new BITMAPINFOHEADER
            {
                Size = sizeof(BITMAPINFOHEADER),
                Width = width,
                Height = -height, // negative: rows top to bottom, whatever the bitmap stores
                Planes = 1,
                BitCount = 32,
                Compression = Gdi32.BI_RGB,
            },
        };

        var screen = User32.GetDC(0);
        try
        {
            fixed (byte* destination = pixels)
            {
                if (Gdi32.GetDIBits(screen, bitmap, 0, (uint)height, destination, ref header, Gdi32.DIB_RGB_COLORS) != height)
                {
                    return null;
                }
            }
        }
        finally
        {
            _ = User32.ReleaseDC(0, screen);
        }

        return WithAlpha(width, height, pixels);
    }

    /// <summary>
    /// A bitmap whose alpha is zero everywhere has no alpha at all (an icon without
    /// transparency, or a thumbnail): it is opaque, not invisible.
    /// </summary>
    internal static IconPixels WithAlpha(int width, int height, byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                return new IconPixels(width, height, pixels, Premultiplied: true);
            }
        }

        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xFF;
        }

        return new IconPixels(width, height, pixels, Premultiplied: false);
    }
}
