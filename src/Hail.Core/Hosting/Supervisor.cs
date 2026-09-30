using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Hail.Core.History;
using Hail.Core.Ports;
using Hail.Core.Ranking;
using Hail.Sdk;

namespace Hail.Core.Hosting;

/// <summary>How long the supervisor waits, and for how much (Hail.md §6.4, §6.5).</summary>
public sealed record SupervisorOptions
{
    /// <summary>
    /// How long the first frame waits for the providers that answer from memory. Whatever has
    /// answered by then is ranked together; anything later joins below the highlighted row.
    /// </summary>
    public TimeSpan FirstFrameBudget { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>After this a provider's query is cancelled, abandoned, and counted as a fault.</summary>
    public TimeSpan HardBudget { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>More rows than anyone reads, and a bound on what one provider can make the host hold.</summary>
    public int MaxResultsPerProvider { get; init; } = 500;

    /// <summary>This many faults inside <see cref="FaultWindow"/> and a provider is switched off.</summary>
    public int FaultLimit { get; init; } = 3;

    public TimeSpan FaultWindow { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>Everything found so far for one query, and whether every provider has answered.</summary>
public sealed record QueryUpdate(IReadOnlyList<ProviderResult> Results, bool IsComplete);

/// <summary>A provider the supervisor has switched off, and why, in words for the tray.</summary>
public sealed record ProviderFault(string ProviderId, string ProviderName, string Reason);

/// <summary>What asking providers to rebuild remembered results found.</summary>
/// <param name="Found">Rebuilt results, in the order they were asked for.</param>
/// <param name="Gone">Keys whose provider says the thing no longer exists; history forgets them.</param>
public sealed record RecallOutcome(IReadOnlyList<ProviderResult> Found, IReadOnlyList<UsageKey> Gone);

/// <summary>
/// Runs a query across its providers (Hail.md §6.4, §6.5). Each provider runs on its own task,
/// after its own debounce, initialised lazily and once, cut off at the hard budget; a provider
/// that throws or overruns costs itself and, after <see cref="SupervisorOptions.FaultLimit"/>
/// faults in a minute, is switched off until Hail restarts.
/// </summary>
/// <remarks>
/// Results arrive a provider at a time. The first update waits, at most for the first-frame
/// budget, for every provider without a debounce, so that the ones answering from memory are
/// ranked against each other; every later update adds a provider that took longer.
/// </remarks>
public sealed class Supervisor
{
    private readonly IReadOnlyList<ProviderRegistration> _providers;
    private readonly IHostLog _log;
    private readonly SupervisorOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Task<bool>> _initialisations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _faultTimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProviderFault> _disabled = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Supervisor(IReadOnlyList<ProviderRegistration> providers, IHostLog log, SupervisorOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(log);
        _providers = providers;
        _log = log;
        _options = options ?? new SupervisorOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised, on whichever thread noticed, when a provider is switched off.</summary>
    public event Action? FaultsChanged;

    public IReadOnlyList<ProviderRegistration> Providers => _providers;

    /// <summary>The providers switched off so far, in the order they were.</summary>
    public IReadOnlyList<ProviderFault> Faults
    {
        get
        {
            lock (_gate)
            {
                return [.. _disabled.Values];
            }
        }
    }

    public bool IsDisabled(string providerId)
    {
        lock (_gate)
        {
            return _disabled.ContainsKey(providerId);
        }
    }

    /// <summary>
    /// Streams what the query's providers find. Cancelling <paramref name="ct"/> (the next
    /// keystroke) cancels every provider still running and ends the stream.
    /// </summary>
    public async IAsyncEnumerable<QueryUpdate> RunAsync(ParsedQuery parsed, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        var targets = parsed.Targets.Where(t => !IsDisabled(t.Registration.Id)).ToArray();
        if (targets.Length == 0)
        {
            yield return new QueryUpdate([], IsComplete: true);
            yield break;
        }

        var channel = Channel.CreateUnbounded<Batch>(new UnboundedChannelOptions { SingleReader = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            foreach (var target in targets)
            {
                _ = RunTargetAsync(target, channel.Writer, stop.Token);
            }

            var collected = new List<ProviderResult>();
            var outstanding = targets.Length;
            var immediate = targets.Count(t => IsImmediate(t.Registration));

            using (var budget = new CancellationTokenSource(_options.FirstFrameBudget, _time))
            using (var firstFrame = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token))
            {
                while (immediate > 0 && outstanding > 0)
                {
                    Batch batch;
                    try
                    {
                        batch = await channel.Reader.ReadAsync(firstFrame.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        break;
                    }

                    collected.AddRange(batch.Results);
                    outstanding--;
                    if (batch.Immediate)
                    {
                        immediate--;
                    }
                }
            }

            yield return new QueryUpdate([.. collected], outstanding == 0);

            while (outstanding > 0)
            {
                var batch = await channel.Reader.ReadAsync(ct).ConfigureAwait(false);
                collected.AddRange(batch.Results);
                outstanding--;

                // Providers that finished together are one update, not one layout each.
                while (outstanding > 0 && channel.Reader.TryRead(out var more))
                {
                    collected.AddRange(more.Results);
                    outstanding--;
                }

                yield return new QueryUpdate([.. collected], outstanding == 0);
            }
        }
        finally
        {
            // The consumer stopped listening (a newer query, or the box hidden): nothing still
            // running is wanted.
            stop.Cancel();
        }
    }

    /// <summary>
    /// Asks the providers of remembered results to rebuild them, for the empty box. A provider
    /// that fails or overruns here counts a fault as it would on a query.
    /// </summary>
    public async Task<RecallOutcome> RecallAsync(IReadOnlyList<UsageKey> keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var outcomes = await Task.WhenAll(keys.Select(key => RecallOneAsync(key, ct))).ConfigureAwait(false);
        return new RecallOutcome(
            [.. outcomes.Where(o => o.Found is not null).Select(o => o.Found!)],
            [.. outcomes.Where(o => o.Gone).Select(o => o.Key)]);
    }

    private static bool IsImmediate(ProviderRegistration registration) => registration.Debounce <= TimeSpan.Zero;

    private async Task RunTargetAsync(QueryTarget target, ChannelWriter<Batch> writer, CancellationToken ct)
    {
        var registration = target.Registration;
        IReadOnlyList<ProviderResult> results = [];
        try
        {
            if (!IsImmediate(registration))
            {
                await Task.Delay(registration.Debounce, _time, ct).ConfigureAwait(false);
            }

            if (await EnsureInitialisedAsync(registration, ct).ConfigureAwait(false))
            {
                results = await CollectWithinBudgetAsync(target, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The query is gone and nothing is reading its channel any more.
            return;
        }
#pragma warning disable CA1031 // The supervisor's own guard: every target must report, or the query never completes.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.LogError($"Running provider {registration.Id} failed inside the host.", ex);
        }

        writer.TryWrite(new Batch(results, IsImmediate(registration)));
    }

    private async Task<IReadOnlyList<ProviderResult>> CollectWithinBudgetAsync(QueryTarget target, CancellationToken ct)
    {
        // Not disposed with a using: a provider that ignores its token outlives this method,
        // and its source is disposed when it finally finishes.
        var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var collect = Task.Run(() => CollectAsync(target, budget.Token), CancellationToken.None);
        _ = collect.ContinueWith(
            finished =>
            {
                _ = finished.Exception;
                budget.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            return await collect.WaitAsync(_options.HardBudget, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            TryCancel(budget);
            RecordFault(
                target.Registration,
                string.Create(CultureInfo.InvariantCulture, $"it did not answer within {_options.HardBudget.TotalSeconds:0.#} s"));
            return [];
        }
    }

    private async Task<IReadOnlyList<ProviderResult>> CollectAsync(QueryTarget target, CancellationToken ct)
    {
        var registration = target.Registration;
        var results = new List<ProviderResult>();
        try
        {
            await foreach (var result in registration.Provider.QueryAsync(target.Query, ct).ConfigureAwait(false))
            {
                if (IsWellFormed(result))
                {
                    results.Add(new ProviderResult(result, target.Order, registration.Id));
                }

                if (results.Count >= _options.MaxResultsPerProvider)
                {
                    break;
                }
            }

            return results;
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
            _log.LogError($"Provider {registration.Id} failed a query of {target.Query.RawText.Length} characters; its results were dropped.", ex);
            RecordFault(registration, "it failed while searching");
            return [];
        }
    }

    private async Task<(UsageKey Key, ProviderResult? Found, bool Gone)> RecallOneAsync(UsageKey key, CancellationToken ct)
    {
        var order = -1;
        for (var i = 0; i < _providers.Count; i++)
        {
            if (string.Equals(_providers[i].Id, key.ProviderId, StringComparison.Ordinal))
            {
                order = i;
                break;
            }
        }

        // A provider that is not here today (switched off in settings, say) keeps its history.
        if (order < 0 || _providers[order] is not { Provider: IRecall recall } registration || IsDisabled(registration.Id))
        {
            return (key, null, false);
        }

        try
        {
            if (!await EnsureInitialisedAsync(registration, ct).ConfigureAwait(false) || !recall.CanRecall)
            {
                return (key, null, false);
            }

            var result = await Task.Run(() => recall.RecallAsync(key.ResultId, ct).AsTask(), CancellationToken.None)
                .WaitAsync(_options.HardBudget, _time, ct)
                .ConfigureAwait(false);

            if (result is null)
            {
                return (key, null, true);
            }

            return IsWellFormed(result) ? (key, new ProviderResult(result, order, registration.Id), false) : (key, null, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            RecordFault(registration, "it did not answer in time when asked for a remembered result");
            return (key, null, false);
        }
#pragma warning disable CA1031 // As in CollectAsync: the provider's failure is its own.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.LogError($"Provider {registration.Id} failed to rebuild a remembered result.", ex);
            RecordFault(registration, "it failed when asked for a remembered result");
            return (key, null, false);
        }
    }

    /// <summary>
    /// Initialises a provider the first time a query reaches it, and never again. A query
    /// waits for it no longer than the hard budget; the initialisation carries on regardless,
    /// and is not tied to the query's token, so a keystroke cannot leave it half done.
    /// </summary>
    private async Task<bool> EnsureInitialisedAsync(ProviderRegistration registration, CancellationToken ct)
    {
        Task<bool> initialisation;
        lock (_gate)
        {
            if (!_initialisations.TryGetValue(registration.Id, out initialisation!))
            {
                // Started on the pool, so a provider whose initialisation blocks before its
                // first await does not do it under this lock.
                initialisation = Task.Run(() => InitialiseAsync(registration), CancellationToken.None);
                _initialisations[registration.Id] = initialisation;
            }
        }

        try
        {
            return await initialisation.WaitAsync(_options.HardBudget, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
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
            _log.LogError($"Provider {registration.Id} failed to start and is switched off until Hail restarts.", ex);
            Disable(registration, "it failed to start");
            return false;
        }
    }

    private void RecordFault(ProviderRegistration registration, string reason)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_disabled.ContainsKey(registration.Id))
            {
                return;
            }

            if (!_faultTimes.TryGetValue(registration.Id, out var times))
            {
                times = new Queue<DateTimeOffset>();
                _faultTimes[registration.Id] = times;
            }

            while (times.Count > 0 && now - times.Peek() > _options.FaultWindow)
            {
                times.Dequeue();
            }

            times.Enqueue(now);
            if (times.Count < _options.FaultLimit)
            {
                _log.LogInfo($"Provider {registration.Id} faulted ({reason}); {times.Count} of {_options.FaultLimit} allowed in a minute.");
                return;
            }
        }

        _log.LogError($"Provider {registration.Id} faulted {_options.FaultLimit} times in a minute ({reason}) and is switched off until Hail restarts.");
        Disable(registration, $"{reason}, {_options.FaultLimit} times in a minute");
    }

    private void Disable(ProviderRegistration registration, string reason)
    {
        lock (_gate)
        {
            if (!_disabled.TryAdd(registration.Id, new ProviderFault(registration.Id, registration.Name, reason)))
            {
                return;
            }
        }

        FaultsChanged?.Invoke();
    }

    private static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The provider finished in the instant after its budget ran out; nothing to stop.
        }
    }

    private static bool IsWellFormed(Result? result) =>
        result is not null
        && !string.IsNullOrWhiteSpace(result.Id)
        && !string.IsNullOrWhiteSpace(result.Title)
        && result.Icon is not null
        && result.Primary is { Execute: not null, Title: not null }
        && result.Secondary is not null
        && result.Secondary.All(a => a is { Execute: not null, Title: not null });

    private sealed record Batch(IReadOnlyList<ProviderResult> Results, bool Immediate);
}
