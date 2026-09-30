using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using Hail.Sdk;
using Hail.Windows.Launching;

namespace Hail.App;

/// <summary>
/// The Windows clipboard for providers, through WPF's own clipboard on the UI thread, which is
/// the apartment the clipboard wants. WPF retries while another program holds it open.
/// </summary>
internal sealed class WpfClipboard(Dispatcher dispatcher) : IClipboard
{
    /// <summary>More than any sum or path; a provider cannot fill the clipboard with megabytes.</summary>
    private const int MaxText = 1024 * 1024;

    public async ValueTask SetTextAsync(string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxText)
        {
            throw new LaunchRefusedException("That is too much text for Hail to copy.");
        }

        await dispatcher.InvokeAsync(() => Clipboard.SetDataObject(text, copy: true), DispatcherPriority.Normal, ct);
    }

    public async ValueTask SetFileAsync(string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!await Task.Run(() => LocalPaths.Exists(path), ct).ConfigureAwait(false))
        {
            throw new LaunchRefusedException("Hail only copies files that are on this PC's own drives and still there.");
        }

        await dispatcher.InvokeAsync(() => Clipboard.SetFileDropList([path]), DispatcherPriority.Normal, ct);
    }
}
