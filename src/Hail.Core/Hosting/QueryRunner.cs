using Hail.Core.Ports;
using Hail.Core.Ranking;
using Hail.Sdk;

namespace Hail.Core.Hosting;

/// <summary>A provider, the context it is given, and its place in the order.</summary>
public sealed record ProviderRegistration(string Id, IProvider Provider, IPluginContext Context);

/// <summary>
/// Runs one query across every provider and collects what they return (Hail.md §6.4). The
/// seed of M1's supervisor: it already initialises each provider lazily and once, and
/// already contains a provider's failure to that provider. Budgets, streaming into the list,
/// and fault accounting across queries arrive in M1.
/// </summary>
public sealed class QueryRunner(IReadOnlyList<ProviderRegistration> providers, IHostLog log)
{
    /// <summary>
    /// More rows than anyone reads, and a bound on what a provider that streams without end
    /// can make the host hold for one keystroke.
    /// </summary>
    public const int MaxResultsPerProvider = 500;

    private readonly Dictionary<string, Task<bool>> _initialisations = [];
    private readonly Lock _gate = new();

    public async Task<IReadOnlyList<ProviderResult>> RunAsync(Query query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var collected = new List<ProviderResult>();
        for (var order = 0; order < providers.Count; order++)
        {
            ct.ThrowIfCancellationRequested();
            var registration = providers[order];

            if (!await EnsureInitialisedAsync(registration, ct).ConfigureAwait(false))
            {
                continue;
            }

            collected.AddRange(await CollectAsync(registration, order, query, ct).ConfigureAwait(false));
        }

        return collected;
    }

    private async Task<IReadOnlyList<ProviderResult>> CollectAsync(
        ProviderRegistration registration,
        int order,
        Query query,
        CancellationToken ct)
    {
        var results = new List<ProviderResult>();
        try
        {
            await foreach (var result in registration.Provider.QueryAsync(query, ct).ConfigureAwait(false))
            {
                if (IsWellFormed(result))
                {
                    results.Add(new ProviderResult(result, order));
                }

                if (results.Count >= MaxResultsPerProvider)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A provider is code the host did not write; any failure is its own.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The query's length, never its text (Hail.md §9).
            log.LogError($"Provider {registration.Id} failed a query of {query.RawText.Length} characters; its results were dropped.", ex);
            return [];
        }

        return results;
    }

    /// <summary>
    /// Initialises a provider the first time a query reaches it, and never again. A failed
    /// initialisation keeps the provider out until Hail restarts (M1 adds Reload), and is
    /// logged once rather than on every keystroke.
    /// </summary>
    private Task<bool> EnsureInitialisedAsync(ProviderRegistration registration, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_initialisations.TryGetValue(registration.Id, out var initialisation))
            {
                // Deliberately not tied to this query's token: a keystroke that cancels the
                // query must not leave the provider half-initialised for the next one.
                initialisation = InitialiseAsync(registration);
                _initialisations[registration.Id] = initialisation;
            }

            return initialisation.WaitAsync(ct);
        }
    }

    private async Task<bool> InitialiseAsync(ProviderRegistration registration)
    {
        try
        {
            await registration.Provider.InitializeAsync(registration.Context, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // As above: the provider's failure is contained to the provider.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.LogError($"Provider {registration.Id} failed to initialise and is disabled until Hail restarts.", ex);
            return false;
        }
    }

    private static bool IsWellFormed(Result? result) =>
        result is not null
        && !string.IsNullOrWhiteSpace(result.Id)
        && !string.IsNullOrWhiteSpace(result.Title)
        && result.Primary is not null
        && result.Icon is not null;
}
