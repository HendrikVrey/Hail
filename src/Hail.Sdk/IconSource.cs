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

    /// <summary>
    /// A glyph from the system's icon font (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets
    /// before it), given as the character that draws it, such as <c>"\uE8EF"</c> for a
    /// calculator. The host draws it in the row's text colour, so it follows light and dark.
    /// </summary>
    public static IconSource ForGlyph(string glyph)
    {
        ArgumentException.ThrowIfNullOrEmpty(glyph);
        return new Glyph(glyph);
    }

    /// <summary>No icon: <see cref="None"/>.</summary>
    public sealed record NoIcon : IconSource;

    /// <summary>The shell's icon for an item: <see cref="ForShellItem"/>.</summary>
    /// <param name="ParsingName">A file path or a shell parsing name.</param>
    public sealed record ShellItem(string ParsingName) : IconSource;

    /// <summary>A character of the system's icon font: <see cref="ForGlyph"/>.</summary>
    /// <param name="Character">The character that draws the glyph.</param>
    public sealed record Glyph(string Character) : IconSource;
}
