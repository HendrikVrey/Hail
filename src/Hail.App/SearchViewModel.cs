using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Hail.Core.Hosting;
using Hail.Core.Ports;
using Hail.Core.Ranking;
using Hail.Sdk;
using Hail.Windows.Apps;

namespace Hail.App;

/// <summary>
/// What the box shows: the rows for the current text, which one is highlighted, and a line of
/// status when there is something to say. Used from the UI thread only.
/// </summary>
internal sealed class SearchViewModel(QueryRunner runner, IconCache icons, IHostLog log) : INotifyPropertyChanged
{
    /// <summary>Hail.md §10.1: up to eight rows.</summary>
    public const int MaxRows = 8;

    /// <summary>A row's icon, in device-independent pixels.</summary>
    private const int IconSize = 32;

    private readonly ResultList _list = new();
    private CancellationTokenSource? _query;
    private Query _current = Query.Global(string.Empty);
    private string? _status;
    private bool _executing;

    public event PropertyChangedEventHandler? PropertyChanged;

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

    /// <summary>Whether anything shows below the field, and so whether the divider does.</summary>
    public bool HasContent => HasRows || HasStatus;

    /// <summary>The scale of the monitor the box is on, for rendering icons at its pixel size.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// Runs a query for <paramref name="text"/>. Every keystroke cancels the one before it
    /// (Hail.md §3.3), and a query that finishes after a newer one started is thrown away.
    /// </summary>
    public async Task SearchAsync(string text)
    {
        _query?.Cancel();
        _query?.Dispose();
        var cancel = new CancellationTokenSource();
        _query = cancel;

        var query = Query.Global(text);
        _current = query;

        if (query.Search.Length == 0)
        {
            Show([], status: null);
            return;
        }

        IReadOnlyList<ProviderResult> found;
        try
        {
            found = await Task.Run(() => runner.RunAsync(query, cancel.Token), cancel.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return;
        }

        if (!ReferenceEquals(_query, cancel))
        {
            return;
        }

        var ranked = Ranker.Rank(found, MaxRows);
        Show(ranked, ranked.Count == 0 ? "No apps match." : null);
        LoadIcons(cancel.Token);
    }

    public void MoveDown() => Move(_list.MoveDown);

    public void MoveUp() => Move(_list.MoveUp);

    public void Select(ResultRow row) => Move(() => _list.Select(Rows.IndexOf(row)));

    /// <summary>
    /// Runs the highlighted row's primary action. Null when nothing ran (no row, or an action
    /// already running); otherwise what the box should do next. A failure is shown in the box
    /// and logged, and the box stays so the user can see it.
    /// </summary>
    public async Task<ActionOutcome?> ExecuteSelectedAsync()
    {
        var selected = _list.Selected;
        if (selected is null || _executing)
        {
            return null;
        }

        _executing = true;
        try
        {
            return await selected.Primary.Execute(new ActionContext(_current), CancellationToken.None).ConfigureAwait(true);
        }
        catch (LaunchRefusedException refused)
        {
            log.LogError($"Refused to start result {selected.Id}.", refused);
            Status = refused.Message;
            return ActionOutcome.KeepOpen;
        }
#pragma warning disable CA1031 // The action is a provider's code; its failure is reported, not fatal.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.LogError($"The action \"{selected.Primary.Title}\" failed for result {selected.Id}.", ex);
            Status = $"Windows could not open {selected.Title}.";
            return ActionOutcome.KeepOpen;
        }
        finally
        {
            _executing = false;
        }
    }

    /// <summary>Forgets the query and its rows, for the next time the box is summoned.</summary>
    public void Reset()
    {
        _query?.Cancel();
        _query?.Dispose();
        _query = null;
        _current = Query.Global(string.Empty);
        Show([], status: null);
    }

    private void Show(IReadOnlyList<Result> results, string? status)
    {
        _list.Replace(results);
        Rows.Clear();
        foreach (var result in results)
        {
            Rows.Add(new ResultRow(result));
        }

        SyncSelection();
        Raise(nameof(HasRows));
        Raise(nameof(HasContent));
        Status = status;
    }

    private void Move(Action move)
    {
        move();
        SyncSelection();
    }

    private void SyncSelection()
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].IsSelected = i == _list.SelectedIndex;
        }
    }

    private void LoadIcons(CancellationToken ct)
    {
        var size = (int)Math.Round(IconSize * Scale);
        foreach (var row in Rows)
        {
            _ = LoadIconAsync(row, size, ct);
        }
    }

    private async Task LoadIconAsync(ResultRow row, int size, CancellationToken ct)
    {
        try
        {
            row.Icon = await icons.GetAsync(row.Result.Icon, size, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The row belongs to a query already replaced; its icon is no longer wanted.
        }
#pragma warning disable CA1031 // A missing icon is cosmetic; it is logged and the row stays.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.LogError($"Loading the icon for result {row.Result.Id} failed.", ex);
        }
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
