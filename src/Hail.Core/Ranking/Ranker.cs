using Hail.Sdk;

namespace Hail.Core.Ranking;

/// <summary>A result, which provider produced it, and that provider's place in the user's order.</summary>
public sealed record ProviderResult(Result Result, int ProviderOrder, string ProviderId);

/// <summary>
/// Orders results for the box (Hail.md §6.6). Deterministic, so a test can say exactly what
/// the user sees: score first, then the provider's place in the order, then the title, then
/// the id.
/// </summary>
/// <remarks>
/// The score is the provider's relevance (which, for everything built in, is how well the
/// text matched) lifted by history: a result picked before for what is being typed now can be
/// worth up to twice its relevance, never more, so history reorders close matches and cannot
/// pull a poor one above a good one.
/// </remarks>
public static class Ranker
{
    /// <summary>The most history can multiply a result's relevance by, less one.</summary>
    public const double MaxHistoryLift = 1.0;

    /// <param name="history">
    /// How strongly history favours a result, 0 to 1; null ranks by relevance alone.
    /// </param>
    public static IReadOnlyList<ProviderResult> Rank(IEnumerable<ProviderResult> results, int limit, Func<ProviderResult, double>? history = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        return results
            .Select(r => (Result: r, Score: Score(r, history)))
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Result.ProviderOrder)
            .ThenBy(r => r.Result.Result.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Result.Result.Id, StringComparer.Ordinal)
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

    /// <summary>A last resort (<see cref="Result.LastResort"/>): shown only once everything else has answered.</summary>
    public static bool IsLastResort(Result result) => Relevance(result) <= Result.LastResort;

    private static double Score(ProviderResult result, Func<ProviderResult, double>? history)
    {
        var relevance = Relevance(result.Result);
        if (history is null)
        {
            return relevance;
        }

        var lift = history(result);
        lift = double.IsNaN(lift) ? 0 : Math.Clamp(lift, 0, 1);
        return relevance * (1 + (MaxHistoryLift * lift));
    }
}
