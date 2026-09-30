using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Hail.Core.History;
using Hail.Core.Hosting;
using Hail.Core.Ports;
using Hail.Core.Ranking;
using Hail.Sdk;

namespace Hail.App;

/// <summary>
/// What the box shows: the rows for the current text, which one is highlighted, the keyword in
/// force, the highlighted row's other actions, and a line of status when there is something to
/// say. Used from the UI thread only.
/// </summary>
/// <remarks>
/// <para>
/// Every keystroke cancels the query before it (Hail.md §3.3). The rows on screen stay until
/// the new query has something to show, so the box does not collapse and regrow between
/// letters; while they belong to the previous text, Enter waits for the current text's rows
/// rather than acting on a row the user is no longer asking for.
/// </para>
/// <para>
/// Results that arrive later join by the no-jump rule (<see cref="ResultList.Merge"/>), and a
/// last-resort row (the web search) waits until every provider has answered.
/// </para>
/// </remarks>
internal sealed class SearchViewModel : INotifyPropertyChanged
{
    /// <summary>Hail.md §10.1: up to eight rows.</summary>
    public const int MaxRows = 8;

    /// <summary>Remembered results asked for to fill an empty box; some may be gone.</summary>
    private const int RecentCandidates = 16;

    /// <summary>A row's icon, in device-independent pixels.</summary>
    private const int IconSize = 32;

    /// <summary>How long Enter pressed on the previous text's rows waits for the current text's.</summary>
    private static readonly TimeSpan EnterWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A question is not answered by an Enter that arrives this soon after it appears: a
    /// double press, or a key held down, must not restart Windows.
    /// </summary>
    private static readonly TimeSpan ConfirmArming = TimeSpan.FromMilliseconds(400);

    private readonly Supervisor _supervisor;
    private readonly QueryParser _parser;
    private readonly UsageHistory _history;
    private readonly IconCache _icons;
    private readonly IHostLog _log;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _remembered;
    private readonly ResultList _list = new();

    private Dictionary<ProviderResult, ResultRow> _rowsByResult = new(ReferenceEqualityComparer.Instance);
    private CancellationTokenSource? _query;
    private ParsedQuery _current;
    private bool _listIsCurrent = true;
    private TaskCompletionSource<bool> _ready = Settled(true);
    private Confirmation? _confirmation;
    private string? _status;
    private string? _scope;
    private IReadOnlyList<ActionHint> _footer = [];
    private bool _executing;

    public SearchViewModel(Supervisor supervisor, QueryParser parser, UsageHistory history, IconCache icons, IHostLog log, TimeProvider? time = null)
    {
        _supervisor = supervisor;
        _parser = parser;
        _history = history;
        _icons = icons;
        _log = log;
        _time = time ?? TimeProvider.System;
        _remembered = [.. supervisor.Providers.Where(p => p.IsRemembered).Select(p => p.Id)];
        _current = parser.Parse(string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// How long an action is waited for before the box stops waiting and says so. Long enough
    /// for Windows' elevation prompt to be answered; short enough that one stuck launch (a
    /// shell extension that never returns) does not leave Enter dead for the session.
    /// </summary>
    internal TimeSpan ActionBudget { get; init; } = TimeSpan.FromSeconds(20);

    public ObservableCollection<ResultRow> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    public string? Status
    {
        get => _status;
        private set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            Raise();
            Raise(nameof(HasStatus));
            Raise(nameof(HasContent));
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(_status);

    /// <summary>The name of the provider a keyword has scoped the query to ("Google"), or null.</summary>
    public string? Scope
    {
        get => _scope;
        private set
        {
            if (_scope == value)
            {
                return;
            }

            _scope = value;
            Raise();
            Raise(nameof(HasScope));
        }
    }

    public bool HasScope => _scope is not null;

    /// <summary>The highlighted row's other actions and their chords (Hail.md §10.1).</summary>
    public IReadOnlyList<ActionHint> Footer
    {
        get => _footer;
        private set
        {
            _footer = value;
            Raise();
            Raise(nameof(HasFooter));
            Raise(nameof(HasContent));
        }
    }

    public bool HasFooter => _footer.Count > 0;

    /// <summary>Whether anything shows below the field, and so whether the divider does.</summary>
    public bool HasContent => HasRows || HasStatus || HasFooter;

    /// <summary>Whether the box is asking the user to confirm something (Escape goes back).</summary>
    public bool IsConfirming => _confirmation is not null;

    /// <summary>The scale of the monitor the box is on, for rendering icons at its pixel size.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>What Tab puts in the box for the highlighted row, or null.</summary>
    public string? Completion => _confirmation is null ? _list.Selected?.Result.Completion : null;

    /// <summary>Whether the highlighted row has an action on <paramref name="gesture"/>.</summary>
    public bool Offers(Gesture gesture) =>
        _confirmation is null && _list.Selected?.Result is { } selected
        && (gesture == Gesture.Enter || selected.Secondary.Any(a => a.Gesture == gesture));

    /// <summary>
    /// Runs a query for <paramref name="text"/>, streaming what the providers find into the
    /// rows. An empty box shows the results picked most, from history.
    /// </summary>
    public async Task SearchAsync(string text)
    {
        var cancel = BeginQuery();
        var parsed = _parser.Parse(text);
        _current = parsed;
        Scope = parsed.Scope?.Label;

        try
        {
            if (parsed.IsEmpty)
            {
                await ShowRecentAsync(cancel).ConfigureAwait(true);
                return;
            }

            await foreach (var update in _supervisor.RunAsync(parsed, cancel.Token).ConfigureAwait(true))
            {
                if (!ReferenceEquals(_query, cancel))
                {
                    return;
                }

                Apply(update);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // A newer keystroke took over.
        }
        finally
        {
            if (ReferenceEquals(_query, cancel))
            {
                _ready.TrySetResult(_listIsCurrent);
            }
        }
    }

    public void MoveDown() => Move(_list.MoveDown);

    public void MoveUp() => Move(_list.MoveUp);

    public void Select(ResultRow row) => Move(() => _list.Select(Rows.IndexOf(row)));

    /// <summary>
    /// Runs the highlighted row's action for <paramref name="gesture"/>. Null when nothing ran;
    /// otherwise what the box should do next. A failure is shown in the box and logged, and
    /// the box stays so the user can read it.
    /// </summary>
    public async Task<ActionOutcome?> ExecuteAsync(Gesture gesture)
    {
        if (_executing)
        {
            return null;
        }

        if (_confirmation is { } confirmation)
        {
            return gesture == Gesture.Enter && _time.GetElapsedTime(confirmation.ShownAt) >= ConfirmArming
                ? await RunAsync(confirmation.Source, confirmation.Question.Confirmed).ConfigureAwait(true)
                : null;
        }

        if (!_listIsCurrent)
        {
            var query = _query;
            bool ready;
            try
            {
                ready = await _ready.Task.WaitAsync(EnterWait).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                ready = false;
            }

            // A second Enter that waited alongside the first finds it already running.
            if (!ready || !ReferenceEquals(query, _query) || _confirmation is not null || _executing)
            {
                return null;
            }
        }

        var selected = _list.Selected;
        var action = selected is null
            ? null
            : gesture == Gesture.Enter
                ? selected.Result.Primary
                : selected.Result.Secondary.FirstOrDefault(a => a.Gesture == gesture);

        return selected is null || action is null ? null : await RunAsync(selected, action).ConfigureAwait(true);
    }

    /// <summary>Leaves a confirmation without running it. False when there was none.</summary>
    public bool CancelConfirmation()
    {
        if (_confirmation is null)
        {
            return false;
        }

        _confirmation = null;
        Raise(nameof(IsConfirming));
        Render();
        return true;
    }

    /// <summary>Forgets the query and its rows, for the next time the box is summoned.</summary>
    public void Reset()
    {
        BeginQuery();
        _ready.TrySetResult(false);
        _current = _parser.Parse(string.Empty);
        Scope = null;
        _list.Clear();
        _listIsCurrent = true;
        Status = null;
        Render();
    }

    private static TaskCompletionSource<bool> Settled(bool value)
    {
        var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        settled.SetResult(value);
        return settled;
    }

    private CancellationTokenSource BeginQuery()
    {
        _query?.Cancel();
        _query?.Dispose();
        var cancel = new CancellationTokenSource();
        _query = cancel;

        _ready.TrySetResult(false);
        _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _listIsCurrent = false;

        if (_confirmation is not null)
        {
            _confirmation = null;
            Raise(nameof(IsConfirming));
            Render();
        }

        return cancel;
    }

    private void Apply(QueryUpdate update)
    {
        var now = _time.GetUtcNow();
        var candidates = update.IsComplete
            ? update.Results
            : [.. update.Results.Where(r => !Ranker.IsLastResort(r.Result))];
        var ranked = Ranker.Rank(candidates, MaxRows, r => Lift(r, now));

        if (!_listIsCurrent)
        {
            // Nothing yet for this text: the previous rows stay rather than the box emptying.
            if (ranked.Count == 0 && !update.IsComplete)
            {
                return;
            }

            _list.Replace(ranked);
            _listIsCurrent = true;
            _ready.TrySetResult(true);
        }
        else
        {
            _list.Merge(ranked, MaxRows);
        }

        Status = update.IsComplete && _list.Items.Count == 0 ? "Nothing found." : null;
        Render();
    }

    private async Task ShowRecentAsync(CancellationTokenSource cancel)
    {
        var keys = _history.Top(RecentCandidates, _time.GetUtcNow());
        IReadOnlyList<ProviderResult> found = [];
        if (keys.Count > 0)
        {
            var outcome = await _supervisor.RecallAsync(keys, cancel.Token).ConfigureAwait(true);
            if (!ReferenceEquals(_query, cancel))
            {
                return;
            }

            if (outcome.Gone.Count > 0)
            {
                _history.Forget(outcome.Gone);
            }

            found = [.. outcome.Found.Take(MaxRows)];
        }

        _list.Replace(found);
        _listIsCurrent = true;
        _ready.TrySetResult(true);
        Status = null;
        Render();
    }

    private double Lift(ProviderResult result, DateTimeOffset now) =>
        _remembered.Contains(result.ProviderId)
            ? _history.Lift(_current.RawText, new UsageKey(result.ProviderId, result.Result.Id), now)
            : 0;

    private async Task<ActionOutcome?> RunAsync(ProviderResult source, ResultAction action)
    {
        _executing = true;

        // Taken now: the app being started can take the foreground before the action returns,
        // which hides and resets the box, and the pick must be remembered for what was typed.
        var typed = _current.RawText;
        var context = new ActionContext(QueryFor(source));

        // The action is a provider's code: it runs off the UI thread, and is waited for no
        // longer than the budget.
        var limit = new CancellationTokenSource(ActionBudget, _time);
        var running = Task.Run(() => action.Execute(context, limit.Token).AsTask(), CancellationToken.None);
        _ = running.ContinueWith(
            done =>
            {
                _ = done.Exception;
                limit.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            var outcome = await running.WaitAsync(ActionBudget, _time).ConfigureAwait(true);
            if (outcome is ActionOutcome.Confirm confirm)
            {
                _confirmation = new Confirmation(confirm, source, _time.GetTimestamp());
                Raise(nameof(IsConfirming));
                Status = null;
                Render();
                return ActionOutcome.KeepOpen;
            }

            Remember(source, typed);
            if (_confirmation is not null)
            {
                _confirmation = null;
                Raise(nameof(IsConfirming));
                Render();
            }

            return outcome;
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && limit.IsCancellationRequested))
        {
            _log.LogError($"\"{action.Title}\" for a result of {source.ProviderId} did not finish within {ActionBudget.TotalSeconds:0} s; Hail stopped waiting.");
            return Failed($"“{action.Title}” is taking too long; Hail stopped waiting for it.");
        }
        catch (LaunchRefusedException refused)
        {
            _log.LogError($"Refused \"{action.Title}\" for a result of {source.ProviderId}: {Redaction.Describe(refused)}");
            return Failed(refused.Message);
        }
#pragma warning disable CA1031 // The action is a provider's code; its failure is reported, not fatal.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Never the message: .NET's own names the address or path it could not open.
            _log.LogError($"\"{action.Title}\" failed for a result of {source.ProviderId}: {Redaction.Describe(ex)}");
            return Failed($"“{action.Title}” did not work for {source.Result.Title}.");
        }
        finally
        {
            _executing = false;
        }
    }

    private ActionOutcome Failed(string message)
    {
        if (_confirmation is not null)
        {
            _confirmation = null;
            Raise(nameof(IsConfirming));
            Render();
        }

        Status = message;
        return ActionOutcome.KeepOpen;
    }

    /// <summary>The query the provider was asked, so an action sees what its result was made for.</summary>
    private Query QueryFor(ProviderResult source) =>
        _current.Targets.FirstOrDefault(t => t.Registration.Id == source.ProviderId)?.Query
        ?? Query.Global(_current.RawText);

    private void Remember(ProviderResult source, string typed)
    {
        if (_remembered.Contains(source.ProviderId))
        {
            _history.Record(typed, new UsageKey(source.ProviderId, source.Result.Id), _time.GetUtcNow());
        }
    }

    /// <remarks>
    /// Ignored while the rows belong to the previous text: the current text's rows replace
    /// them with the first one highlighted, and a move made on the old rows would be lost under
    /// an Enter that then ran a row the user had moved away from.
    /// </remarks>
    private void Move(Action move)
    {
        if (_confirmation is not null || !_listIsCurrent)
        {
            return;
        }

        move();
        SyncSelection();
    }

    private void Render()
    {
        List<ResultRow> rows;
        if (_confirmation is { } confirmation)
        {
            rows = [new ResultRow(confirmation.AsResult())];
        }
        else
        {
            var reused = new Dictionary<ProviderResult, ResultRow>(ReferenceEqualityComparer.Instance);
            foreach (var item in _list.Items)
            {
                reused[item] = _rowsByResult.TryGetValue(item, out var row) ? row : new ResultRow(item.Result);
            }

            _rowsByResult = reused;
            rows = [.. _list.Items.Select(item => reused[item])];
        }

        if (!rows.SequenceEqual(Rows))
        {
            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(row);
            }

            Raise(nameof(HasRows));
            Raise(nameof(HasContent));
        }

        SyncSelection();
        LoadIcons(_query?.Token ?? CancellationToken.None);
    }

    private void SyncSelection()
    {
        var selected = _confirmation is null ? _list.SelectedIndex : 0;
        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].IsSelected = i == selected;
        }

        var footer = selected >= 0 && selected < Rows.Count && _confirmation is null ? Rows[selected].Secondary : [];
        if (!footer.SequenceEqual(_footer))
        {
            Footer = footer;
        }
    }

    private void LoadIcons(CancellationToken ct)
    {
        var size = (int)Math.Round(IconSize * Scale);
        foreach (var row in Rows)
        {
            if (row.Icon is null && !row.HasGlyph)
            {
                _ = LoadIconAsync(row, size, ct);
            }
        }
    }

    private async Task LoadIconAsync(ResultRow row, int size, CancellationToken ct)
    {
        try
        {
            row.Icon = await _icons.GetAsync(row.Result.Icon, size, ct).ConfigureAwait(true);
            row.IconMissing = row.Icon is null && row.Result.Icon is IconSource.ShellItem;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The row belongs to a query already replaced; its icon is no longer wanted.
        }
#pragma warning disable CA1031 // A missing icon is cosmetic; it is logged and the row stays.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.LogError("Loading a row's icon failed.", ex);
        }
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>A question an action asked (Restart Windows?), the row that asked it, and when.</summary>
    private sealed record Confirmation(ActionOutcome.Confirm Question, ProviderResult Source, long ShownAt)
    {
        public Result AsResult() =>
            new(
                Id: "confirm",
                Title: Question.Question,
                Subtitle: "Enter to go ahead, Escape to go back",
                Icon: Source.Result.Icon,
                Relevance: 1,
                Primary: Question.Confirmed,
                Secondary: [],
                Highlight: null);
    }
}
