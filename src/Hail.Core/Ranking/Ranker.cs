using Hail.Sdk;

namespace Hail.Core.Ranking;

/// <summary>A result, and the position of the provider that produced it in the user's order.</summary>
public sealed record ProviderResult(Result Result, int ProviderOrder);

/// <summary>
/// Orders results for the box (Hail.md §6.6). Deterministic, so a test can say exactly what
/// the user sees: relevance first, then the provider's place in the order, then the title,
/// then the id.
/// </summary>
/// <remarks>
/// M0 ranks by the providers' own relevance. History (frecency) and the user's provider
/// weights multiply into it in M1; they belong here, which is why this is a type of its own
/// with one caller.
/// </remarks>
public static class Ranker
{
    public static IReadOnlyList<Result> Rank(IEnumerable<ProviderResult> results, int limit)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        return results
            .OrderByDescending(r => Relevance(r.Result))
            .ThenBy(r => r.ProviderOrder)
            .ThenBy(r => r.Result.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Result.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(r => r.Result)
            .ToArray();
    }

    /// <summary>
    /// A provider's relevance, held to 0..1. The range is part of the contract, but the value
    /// comes from code the host did not write, so it is clamped rather than trusted, and a
    /// NaN sinks to the bottom rather than poisoning the sort.
    /// </summary>
    public static double Relevance(Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return double.IsNaN(result.Relevance) ? 0 : Math.Clamp(result.Relevance, 0, 1);
    }
}
