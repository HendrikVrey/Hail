using Hail.Sdk;

namespace Hail.Plugins;

/// <summary>
/// Stands between a plugin's actions and everything of the host's that holds a result: the
/// box, its rows, WPF's own caches behind them (Hail.md §6.3, §20). Each action the plugin
/// hands over is replaced by one of the host's, which calls the plugin's through a cell;
/// <see cref="Revoke"/> empties every cell when the plugin is unloaded.
/// </summary>
/// <remarks>
/// A plugin's context can only be unloaded when nothing refers to its code, and a result's
/// action is a delegate into that code. The box lets go of its rows when told to, but the
/// window that drew them was measured holding one for longer than any wait is worth: the
/// same reload unloaded one run in four and not the next. With the lease, whatever the UI
/// still holds refers to the host's cell, and the cell to nothing.
/// </remarks>
internal sealed class ActionLease
{
    /// <summary>Dead cells are forgotten every time this many more have been handed out.</summary>
    private const int PruneEvery = 256;

    private readonly Lock _gate = new();
    private readonly List<WeakReference<Cell>> _cells = [];
    private int _sincePrune;
    private bool _revoked;

    /// <summary>
    /// The result with every action routed through a cell of this lease, and its list of
    /// secondary actions copied (a plugin may hand over a list of its own type). A result that
    /// is not well formed is passed on as it is, for the supervisor to drop.
    /// </summary>
    public Result Adopt(Result result)
    {
        if (result?.Primary is null || result.Secondary is null || result.Secondary.Any(a => a is null))
        {
            return result!;
        }

        return result with
        {
            Primary = Wrap(result.Primary),
            Secondary = [.. result.Secondary.Select(Wrap)],
        };
    }

    /// <summary>Empties every cell: an action run after this says the plugin was reloaded.</summary>
    public void Revoke()
    {
        lock (_gate)
        {
            _revoked = true;
            foreach (var weak in _cells)
            {
                if (weak.TryGetTarget(out var cell))
                {
                    cell.Clear();
                }
            }

            _cells.Clear();
        }
    }

    private ResultAction Wrap(ResultAction action)
    {
        if (action.Execute is null)
        {
            return action;
        }

        var cell = new Cell(this, action.Execute);
        lock (_gate)
        {
            if (_revoked)
            {
                cell.Clear();
            }
            else
            {
                _cells.Add(new WeakReference<Cell>(cell));
                if (++_sincePrune >= PruneEvery)
                {
                    _sincePrune = 0;
                    _cells.RemoveAll(w => !w.TryGetTarget(out _));
                }
            }
        }

        return new ResultAction(action.Title, action.Gesture, cell.RunAsync);
    }

    private sealed class Cell(ActionLease lease, Func<IActionContext, CancellationToken, ValueTask<ActionOutcome>> execute)
    {
        private Func<IActionContext, CancellationToken, ValueTask<ActionOutcome>>? _execute = execute;

        public void Clear() => Volatile.Write(ref _execute, null);

        public async ValueTask<ActionOutcome> RunAsync(IActionContext context, CancellationToken ct)
        {
            var execute = Volatile.Read(ref _execute)
                ?? throw new LaunchRefusedException("That plugin was reloaded since this row appeared; search again.");

            var outcome = await execute(context, ct).ConfigureAwait(false);

            // A question's answer is the plugin's action too, and the box holds it while it asks.
            return outcome is ActionOutcome.Confirm { Confirmed: not null } confirm
                ? ActionOutcome.AskFirst(confirm.Question, lease.Wrap(confirm.Confirmed))
                : outcome;
        }
    }
}
