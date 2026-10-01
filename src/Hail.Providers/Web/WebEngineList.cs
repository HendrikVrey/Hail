using Hail.Core.Ports;

namespace Hail.Providers.Web;

/// <summary>
/// The rules an edited list of web engines must pass before it is saved (Hail.md §7.4: a template
/// is refused when it is saved, not when it is used). The provider still checks each engine as it
/// loads, for a list edited by hand.
/// </summary>
public static class WebEngineList
{
    public const int MaxNameLength = 40;
    public const int MaxKeywordLength = 12;
    public const int MaxEngines = 30;

    /// <summary>
    /// The first problem with <paramref name="engines"/>, as a sentence, or null when they can be saved.
    /// </summary>
    /// <param name="engines">The engines as the user left them; names, keywords and templates are trimmed.</param>
    /// <param name="defaultKeyword">The keyword of the engine the last row searches with.</param>
    /// <param name="taken">Keywords other providers answer to, each with the provider's name.</param>
    public static string? Check(IReadOnlyList<WebEngineSetting> engines, string? defaultKeyword, IReadOnlyDictionary<string, string> taken)
    {
        ArgumentNullException.ThrowIfNull(engines);
        ArgumentNullException.ThrowIfNull(taken);

        if (engines.Count > MaxEngines)
        {
            return $"Hail keeps at most {MaxEngines} engines.";
        }

        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < engines.Count; i++)
        {
            var engine = engines[i];
            var label = string.IsNullOrWhiteSpace(engine.Name) ? $"Engine {i + 1}" : engine.Name.Trim();

            if (string.IsNullOrWhiteSpace(engine.Name))
            {
                return $"Engine {i + 1} needs a name.";
            }

            if (engine.Name.Trim().Length > MaxNameLength)
            {
                return $"{label}'s name is longer than {MaxNameLength} characters.";
            }

            var keyword = engine.Keyword?.Trim() ?? string.Empty;
            if (keyword.Length == 0)
            {
                return $"{label} needs a keyword, such as g for Google.";
            }

            if (keyword.Length > MaxKeywordLength || !keyword.All(char.IsLetterOrDigit))
            {
                return $"{label}'s keyword must be letters or digits, at most {MaxKeywordLength} of them.";
            }

            if (!keywords.Add(keyword))
            {
                return $"Two engines use the keyword \"{keyword}\".";
            }

            if (taken.TryGetValue(keyword, out var owner))
            {
                return $"\"{keyword}\" is already {owner}'s keyword.";
            }

            if (WebTemplate.TryCreate(engine.Template?.Trim(), out var problem) is null)
            {
                return $"{label}'s address cannot be used: {problem}.";
            }
        }

        if (engines.Count > 0 && (defaultKeyword is null || !keywords.Contains(defaultKeyword.Trim())))
        {
            return "Choose which engine the last row searches with.";
        }

        return null;
    }
}
