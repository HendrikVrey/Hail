using Hail.Core.Ports;

namespace Hail.Core.Settings;

/// <summary>
/// What <c>%LOCALAPPDATA%\Hail\settings.json</c> holds. Read field by field, so one bad value
/// falls back to its default alone and the rest are kept (Hail.md §10.4, Drift's rule). The
/// settings window writes it and applies each change at once; a hand edit is read at the next
/// start.
/// </summary>
/// <param name="KeepLastQuery">Whether the box keeps its text when it hides (Hail.md §5).</param>
/// <param name="WebSearch">The engines, and which one the fallback row uses.</param>
/// <param name="DisabledProviders">Providers switched off, by id (<c>hail.files</c>, say).</param>
public sealed record HailSettings(bool KeepLastQuery, WebSearchOptions WebSearch, IReadOnlySet<string> DisabledProviders)
{
    public static HailSettings Default { get; } = new(
        KeepLastQuery: false,
        WebSearchOptions.Default,
        new HashSet<string>(StringComparer.Ordinal));

    /// <summary>The shortcut that shows the box (Hail.md §8).</summary>
    public Chord Hotkey { get; init; } = Chord.AltSpace;

    /// <summary>
    /// Whether Hail may ask GitHub once a day for a new version: null until the user has
    /// answered, and nothing is sent until they say yes (Hail.md §9).
    /// </summary>
    public bool? CheckForUpdates { get; init; }

    public bool IsEnabled(string providerId) => !DisabledProviders.Contains(providerId);

    /// <summary>This, with <paramref name="providerId"/> switched on or off.</summary>
    public HailSettings WithProvider(string providerId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var disabled = new HashSet<string>(DisabledProviders, StringComparer.Ordinal);
        if (enabled)
        {
            disabled.Remove(providerId);
        }
        else
        {
            disabled.Add(providerId);
        }

        return this with { DisabledProviders = disabled };
    }
}
