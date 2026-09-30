namespace Hail.Providers.Web;

/// <summary>
/// A search engine's address with <see cref="Placeholder"/> where the search goes (Hail.md
/// §7.4). Checked once, when it is read, so an engine that could send a search somewhere
/// unexpected is refused before anyone types into it.
/// </summary>
public sealed class WebTemplate
{
    public const string Placeholder = "{query}";

    private const int MaxLength = 2048;
    private const string Marker = "hailqueryplaceholder";

    private readonly string _template;

    private WebTemplate(string template) => _template = template;

    /// <summary>
    /// The template, or null with <paramref name="problem"/> saying why not. A template must be
    /// an absolute https address with the placeholder exactly once, in its path or query
    /// string: never in the host, where a search could pick the server, and never before the
    /// host, where it could smuggle in credentials.
    /// </summary>
    public static WebTemplate? TryCreate(string? template, out string problem)
    {
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(template) || template.Length > MaxLength)
        {
            problem = "it is empty or too long";
            return null;
        }

        var first = template.IndexOf(Placeholder, StringComparison.Ordinal);
        if (first < 0 || template.IndexOf(Placeholder, first + 1, StringComparison.Ordinal) >= 0)
        {
            problem = $"it must contain {Placeholder} exactly once";
            return null;
        }

        if (!Uri.TryCreate(template.Replace(Placeholder, Marker, StringComparison.Ordinal), UriKind.Absolute, out var probe))
        {
            problem = "it is not a web address";
            return null;
        }

        if (probe.Scheme != Uri.UriSchemeHttps)
        {
            problem = "it must start with https://";
            return null;
        }

        if (probe.UserInfo.Length > 0 || probe.Host.Contains(Marker, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"{Placeholder} must be in the address's path or query, not its server";
            return null;
        }

        if (!(probe.AbsolutePath + probe.Query).Contains(Marker, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"{Placeholder} must be in the address's path or query";
            return null;
        }

        return new WebTemplate(template);
    }

    /// <summary>The address for <paramref name="search"/>, the search percent-encoded whole.</summary>
    public Uri UriFor(string search)
    {
        ArgumentNullException.ThrowIfNull(search);
        return new Uri(_template.Replace(Placeholder, Uri.EscapeDataString(search), StringComparison.Ordinal), UriKind.Absolute);
    }
}
