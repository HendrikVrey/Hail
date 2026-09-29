using Hail.Sdk;

namespace Hail.Core.Ranking;

/// <summary>
/// The rows in the box and which one is highlighted. Enter runs the highlighted row, so this
/// is the state a mistake here would turn into launching the wrong thing.
/// </summary>
/// <remarks>
/// The movement clamps rather than wraps: Down on the last row stays there, so holding the
/// key never lands somewhere surprising. M1's no-jump merge (Hail.md §3.2) lives here too.
/// </remarks>
public sealed class ResultList
{
    private IReadOnlyList<Result> _items = [];

    public IReadOnlyList<Result> Items => _items;

    /// <summary>The highlighted row's index, or -1 when there are no rows.</summary>
    public int SelectedIndex { get; private set; } = -1;

    public Result? Selected => SelectedIndex >= 0 ? _items[SelectedIndex] : null;

    /// <summary>A new query's results: the highlight goes to the best one.</summary>
    public void Replace(IReadOnlyList<Result> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items;
        SelectedIndex = items.Count > 0 ? 0 : -1;
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
