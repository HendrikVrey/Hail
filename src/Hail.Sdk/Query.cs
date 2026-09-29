namespace Hail.Sdk;

/// <summary>What the user has typed, as one provider sees it.</summary>
/// <param name="RawText">Exactly what is in the box.</param>
/// <param name="Search">
/// <paramref name="RawText"/> with this provider's keyword removed, trimmed.
/// </param>
/// <param name="Keyword">
/// "g" in "g cats"; null when the provider is running globally.
/// </param>
/// <param name="IsKeywordScoped">True when the user asked for this provider by keyword.</param>
public sealed record Query(string RawText, string Search, string? Keyword, bool IsKeywordScoped)
{
    /// <summary>A query that reaches every global provider, with no keyword.</summary>
    public static Query Global(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        return new Query(rawText, rawText.Trim(), Keyword: null, IsKeywordScoped: false);
    }
}
