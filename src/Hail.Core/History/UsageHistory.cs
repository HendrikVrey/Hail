namespace Hail.Core.History;

/// <summary>Which result was picked: the provider's id and the result's own.</summary>
public readonly record struct UsageKey(string ProviderId, string ResultId);

/// <summary>History as it is written to disk. Plain data; <c>Hail.Persistence</c> serialises it.</summary>
public sealed record UsageSnapshot(IReadOnlyList<UsageEntry> Entries)
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
}

/// <param name="Uses">Newest last.</param>
public sealed record UsageEntry(string ProviderId, string ResultId, IReadOnlyList<UsageUse> Uses);

/// <param name="Query">What was typed, normalised and cut short (<see cref="UsageHistory.Normalise"/>).</param>
public sealed record UsageUse(string Query, DateTimeOffset At);

/// <summary>
/// What the user picks, and for what they had typed (Hail.md §6.6): frecency. A result picked
/// for <c>v</c> is favoured next time for <c>v</c> and for <c>vs</c>, more so the more often
/// and the more recently it was picked. Thread-safe.
/// </summary>
/// <remarks>
/// <para>
/// Only providers that implement <c>IRecall</c> are recorded (the caller's rule), so a web
/// search or a sum, whose ids carry what was typed, never reaches the disk. What is kept of the
/// query itself is its first <see cref="MaxQueryLength"/> characters, lower-cased: enough to
/// tell <c>v</c> from <c>vs</c>, which is all frecency needs, and no more.
/// </para>
/// <para>
/// Bounded three ways: <see cref="MaxEntries"/> results, <see cref="MaxUsesPerEntry"/> uses of
/// each, and uses older than <see cref="MaxAge"/> forgotten, so the file stays small without
/// anyone clearing it.
/// </para>
/// </remarks>
public sealed class UsageHistory
{
    public const int MaxEntries = 400;
    public const int MaxUsesPerEntry = 16;
    public const int MaxQueryLength = 16;
    public const int MaxIdLength = 1024;

    /// <summary>A pick counts half as much after this long.</summary>
    public static readonly TimeSpan HalfLife = TimeSpan.FromDays(14);

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(180);

    private readonly Dictionary<UsageKey, List<UsageUse>> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>Raised after every change, on the thread that made it, so the host can save.</summary>
    public event Action? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// The query as history keeps it: trimmed, runs of spaces made one, lower-cased in the
    /// invariant culture, and cut to <see cref="MaxQueryLength"/> characters.
    /// </summary>
    public static string Normalise(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var joined = string.Join(' ', words).ToLowerInvariant();
        return joined.Length > MaxQueryLength ? joined[..MaxQueryLength].TrimEnd() : joined;
    }

    public static UsageHistory FromSnapshot(UsageSnapshot? snapshot, DateTimeOffset now)
    {
        var history = new UsageHistory();
        if (snapshot is null)
        {
            return history;
        }

        foreach (var entry in snapshot.Entries ?? [])
        {
            if (entry is null || !IsValidKey(entry.ProviderId, entry.ResultId))
            {
                continue;
            }

            var uses = (entry.Uses ?? [])
                .Where(u => u is not null && u.Query is not null && now - u.At <= MaxAge)
                .Select(u => new UsageUse(Normalise(u.Query), u.At > now ? now : u.At))
                .OrderBy(u => u.At)
                .TakeLast(MaxUsesPerEntry)
                .ToList();

            if (uses.Count > 0)
            {
                history._entries[new UsageKey(entry.ProviderId, entry.ResultId)] = uses;
            }
        }

        history.Prune(now);
        return history;
    }

    public UsageSnapshot ToSnapshot()
    {
        lock (_gate)
        {
            return new UsageSnapshot(
                [.. _entries
                    .OrderBy(e => e.Key.ProviderId, StringComparer.Ordinal)
                    .ThenBy(e => e.Key.ResultId, StringComparer.Ordinal)
                    .Select(e => new UsageEntry(e.Key.ProviderId, e.Key.ResultId, [.. e.Value]))]);
        }
    }

    public void Record(string query, UsageKey key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!IsValidKey(key.ProviderId, key.ResultId))
        {
            return;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var uses))
            {
                uses = [];
                _entries[key] = uses;
            }

            uses.Add(new UsageUse(Normalise(query), now));
            if (uses.Count > MaxUsesPerEntry)
            {
                uses.RemoveRange(0, uses.Count - MaxUsesPerEntry);
            }

            Prune(now);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// How strongly history favours <paramref name="key"/> for <paramref name="query"/>, 0 to 1.
    /// A pick counts when what was typed then and what is typed now are one a prefix of the
    /// other: picked for <c>v</c>, it counts for <c>vs</c>; picked for <c>vs</c>, it counts
    /// for <c>v</c>. Each counts less the older it is, and the sum saturates, so ten picks are
    /// worth little more than five.
    /// </summary>
    public double Lift(string query, UsageKey key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);
        var typed = Normalise(query);

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var uses))
            {
                return 0;
            }

            var weight = uses.Where(u => Related(u.Query, typed)).Sum(u => Decay(now - u.At));
            return Saturate(weight);
        }
    }

    /// <summary>
    /// The results picked most, most recently, whatever was typed: what an empty box shows.
    /// With <paramref name="providerId"/>, that provider's only.
    /// </summary>
    public IReadOnlyList<UsageKey> Top(int limit, DateTimeOffset now, string? providerId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        lock (_gate)
        {
            return [.. _entries
                .Where(e => providerId is null || string.Equals(e.Key.ProviderId, providerId, StringComparison.Ordinal))
                .Select(e => (e.Key, Weight: e.Value.Sum(u => Decay(now - u.At))))
                .OrderByDescending(e => e.Weight)
                .ThenBy(e => e.Key.ProviderId, StringComparer.Ordinal)
                .ThenBy(e => e.Key.ResultId, StringComparer.Ordinal)
                .Take(limit)
                .Select(e => e.Key)];
        }
    }

    /// <summary>Forgets results that no longer exist.</summary>
    public void Forget(IEnumerable<UsageKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var changed = false;
        lock (_gate)
        {
            foreach (var key in keys)
            {
                changed |= _entries.Remove(key);
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }

        Changed?.Invoke();
    }

    /// <remarks>
    /// A pick from the empty box (typed nothing) counts towards the most-picked list and lifts
    /// nothing typed: every text starts with the empty one, and it would otherwise lift that
    /// result for everything it matches.
    /// </remarks>
    private static bool Related(string then, string now) =>
        now.Length == 0
        || (then.Length > 0
            && (then.StartsWith(now, StringComparison.Ordinal) || now.StartsWith(then, StringComparison.Ordinal)));

    private static double Decay(TimeSpan age) =>
        age <= TimeSpan.Zero ? 1 : Math.Pow(0.5, age / HalfLife);

    /// <summary>One fresh pick is worth about 0.4, three about 0.8, and nothing reaches 1.</summary>
    private static double Saturate(double weight) => 1 - Math.Exp(-weight / 2);

    private static bool IsValidKey(string? providerId, string? resultId) =>
        !string.IsNullOrWhiteSpace(providerId)
        && !string.IsNullOrWhiteSpace(resultId)
        && providerId.Length <= MaxIdLength
        && resultId.Length <= MaxIdLength;

    /// <summary>Drops uses past <see cref="MaxAge"/>, then the weakest results past <see cref="MaxEntries"/>.</summary>
    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, uses) in _entries.ToArray())
        {
            uses.RemoveAll(u => now - u.At > MaxAge);
            if (uses.Count == 0)
            {
                _entries.Remove(key);
            }
        }

        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        var weakest = _entries
            .OrderBy(e => e.Value.Sum(u => Decay(now - u.At)))
            .ThenBy(e => e.Key.ProviderId, StringComparer.Ordinal)
            .ThenBy(e => e.Key.ResultId, StringComparer.Ordinal)
            .Take(_entries.Count - MaxEntries)
            .Select(e => e.Key)
            .ToArray();

        foreach (var key in weakest)
        {
            _entries.Remove(key);
        }
    }
}
