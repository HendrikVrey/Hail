using Hail.Core.Ports;

namespace Hail.Core.Settings;

/// <summary>
/// What <c>%LOCALAPPDATA%\Hail\settings.json</c> holds. Read at startup, field by field, so one
/// bad value falls back to its default alone and the rest are kept (Hail.md §10.4, Drift's
/// rule). The settings window arrives in M3; until then the file is edited by hand and Hail is
/// restarted to read it.
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

    public bool IsEnabled(string providerId) => !DisabledProviders.Contains(providerId);
}
