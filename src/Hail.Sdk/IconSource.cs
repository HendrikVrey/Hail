namespace Hail.Sdk;

/// <summary>
/// Describes an icon as data. The host draws it, off the UI thread and cached, so a provider
/// never loads an image and the contract never names a UI framework.
/// </summary>
public abstract record IconSource
{
    private IconSource()
    {
    }

    /// <summary>No icon; the row keeps the space so titles stay aligned.</summary>
    public static IconSource None { get; } = new NoIcon();

    /// <summary>
    /// The shell's own icon for an item: a file path, or a shell parsing name such as
    /// <c>shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App</c>.
    /// </summary>
    public static IconSource ForShellItem(string parsingName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parsingName);
        return new ShellItem(parsingName);
    }

    public sealed record NoIcon : IconSource;

    public sealed record ShellItem(string ParsingName) : IconSource;
}
