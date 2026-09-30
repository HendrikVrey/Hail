namespace Hail.Core.Ranking;

/// <summary>
/// The rows in the box and which one is highlighted. Enter runs the highlighted row, so this
/// is the state a mistake here would turn into launching the wrong thing.
/// </summary>
/// <remarks>
/// <para>
/// The movement clamps rather than wraps: Down on the last row stays there, so holding the
/// key never lands somewhere surprising.
/// </para>
/// <para>
/// Results that arrive late are merged by the no-jump rule (Hail.md §3.2): the highlighted row
/// and every row above it stay exactly where they are until the next keystroke, and only the
/// rows below are ranked again. Pressing Enter on a row that has just become a different row
/// cannot happen.
/// </para>
/// </remarks>
public sealed class ResultList
{
    private IReadOnlyList<ProviderResult> _items = [];

    public IReadOnlyList<ProviderResult> Items => _items;

    /// <summary>The highlighted row's index, or -1 when there are no rows.</summary>
    public int SelectedIndex { get; private set; } = -1;

    public ProviderResult? Selected => SelectedIndex >= 0 ? _items[SelectedIndex] : null;

    /// <summary>A new query's results: the highlight goes to the best one.</summary>
    public void Replace(IReadOnlyList<ProviderResult> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items;
        SelectedIndex = items.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// More results for the same query, already ranked. Rows up to and including the
    /// highlighted one are kept in place; the rest of the list is <paramref name="ranked"/>
    /// without them, up to <paramref name="limit"/> rows in all.
    /// </summary>
    /// <remarks>
    /// A row is the same row when it is the same result object: the supervisor hands back the
    /// objects it already gave, so identity is exact and two providers' equal ids cannot be
    /// confused.
    /// </remarks>
    public void Merge(IReadOnlyList<ProviderResult> ranked, int limit)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        if (SelectedIndex < 0)
        {
            Replace([.. ranked.Take(limit)]);
            return;
        }

        var kept = _items.Take(SelectedIndex + 1).ToList();
        var keptSet = new HashSet<ProviderResult>(kept, ReferenceEqualityComparer.Instance);
        var merged = new List<ProviderResult>(kept);
        foreach (var result in ranked)
        {
            if (merged.Count >= limit)
            {
                break;
            }

            if (!keptSet.Contains(result))
            {
                merged.Add(result);
            }
        }

        _items = merged;
    }

    public void Clear() => Replace([]);

    public void MoveDown() => Select(SelectedIndex + 1);

    public void MoveUp() => Select(SelectedIndex - 1);

    /// <summary>Highlights row <paramref name="index"/>, clamped to the rows there are.</summary>
    public void Select(int index)
    {
        if (_items.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Clamp(index, 0, _items.Count - 1);
    }
}
