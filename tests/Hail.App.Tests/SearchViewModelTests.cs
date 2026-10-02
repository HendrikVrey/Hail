using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Hail.Core.History;
using Hail.Core.Hosting;
using Hail.Core.Matching;
using Hail.Core.Ports;
using Hail.Sdk;
using Hail.Windows;
using Hail.Windows.Icons;

namespace Hail.App.Tests;

/// <summary>
/// Which row Enter runs, and what history records, with the view model on a WPF dispatcher
/// thread as it runs in the box. Every test types into it as a user would: one query after
/// another, the next started before the last has finished.
/// </summary>
public sealed class SearchViewModelTests
{
    private static readonly TimeSpan FilesDebounce = TimeSpan.FromMilliseconds(150);

    private readonly List<string> _ran = [];

    [Fact]
    public void Enter_on_the_previous_texts_rows_runs_the_current_texts_top_row()
    {
        Ui.Run(async () =>
        {
            using var box = new Box(
                Register("apps", Rows(q => q.Search == "a" ? [Row("old")] : [])),
                Register("files", Rows(q => q.Search == "b" ? [Row("new")] : []), FilesDebounce));

            await box.Model.SearchAsync("a");
            Assert.Equal("old", box.Model.Rows[0].Title);

            var typing = box.Model.SearchAsync("b");
            Assert.Equal("old", box.Model.Rows[0].Title); // still on screen, not collapsed
            var outcome = await box.Model.ExecuteAsync(Gesture.Enter);
            await typing;

            Assert.Equal(ActionOutcome.Hide, outcome);
            Assert.Equal(["new"], _ran);
        });
    }

    [Fact]
    public void Two_enters_while_waiting_run_the_row_once()
    {
        Ui.Run(async () =>
        {
            using var box = new Box(
                Register("apps", Rows(q => q.Search == "a" ? [Row("old")] : [])),
                Register("files", Rows(q => q.Search == "b" ? [Row("new")] : []), FilesDebounce));
            await box.Model.SearchAsync("a");

            var typing = box.Model.SearchAsync("b");
            var first = box.Model.ExecuteAsync(Gesture.Enter);
            var second = box.Model.ExecuteAsync(Gesture.Enter);
            await Task.WhenAll(first, second, typing);

            Assert.Equal(["new"], _ran);
        });
    }

    [Fact]
    public void A_move_on_the_previous_texts_rows_does_not_carry_over()
    {
        Ui.Run(async () =>
        {
            using var box = new Box(
                Register("apps", Rows(q => q.Search == "a" ? [Row("a1"), Row("a2", 0.8)] : [])),
                Register("files", Rows(q => q.Search == "b" ? [Row("b1"), Row("b2", 0.8)] : []), FilesDebounce));
            await box.Model.SearchAsync("a");

            var typing = box.Model.SearchAsync("b");
            box.Model.MoveDown();

            // The highlight does not move on rows that are about to be replaced, so it cannot
            // look as if Enter will run a2.
            Assert.True(box.Model.Rows[0].IsSelected);
            await box.Model.ExecuteAsync(Gesture.Enter);
            await typing;

            Assert.Equal(["b1"], _ran);
        });
    }

    [Fact]
    public void A_question_is_not_answered_by_an_enter_right_after_it()
    {
        Ui.Run(async () =>
        {
            var time = new ManualTime();
            var confirmed = new ResultAction("Restart", Gesture.Enter, (_, _) => Ran("restarted"));
            var restart = Row("Restart", run: () => ValueTask.FromResult(ActionOutcome.AskFirst("Restart Windows?", confirmed)));
            using var box = new Box([Register("commands", Rows(_ => [restart]))], time: time);
            await box.Model.SearchAsync("restart");

            Assert.Equal(ActionOutcome.KeepOpen, await box.Model.ExecuteAsync(Gesture.Enter));
            Assert.True(box.Model.IsConfirming);
            Assert.Equal("Restart Windows?", Assert.Single(box.Model.Rows).Title);

            Assert.Null(await box.Model.ExecuteAsync(Gesture.Enter)); // a double press
            Assert.Empty(_ran);

            time.Advance(TimeSpan.FromMilliseconds(500));
            Assert.Equal(ActionOutcome.Hide, await box.Model.ExecuteAsync(Gesture.Enter));
            Assert.Equal(["restarted"], _ran);
        });
    }

    [Fact]
    public void Escape_leaves_a_question_without_answering_it()
    {
        Ui.Run(async () =>
        {
            var time = new ManualTime();
            var confirmed = new ResultAction("Restart", Gesture.Enter, (_, _) => Ran("restarted"));
            var restart = Row("Restart", run: () => ValueTask.FromResult(ActionOutcome.AskFirst("Restart Windows?", confirmed)));
            using var box = new Box([Register("commands", Rows(_ => [restart]))], time: time);
            await box.Model.SearchAsync("restart");
            await box.Model.ExecuteAsync(Gesture.Enter);

            Assert.True(box.Model.CancelConfirmation());
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.False(box.Model.IsConfirming);

            // Enter now runs the row again, which asks again: nothing has been restarted.
            Assert.Equal(ActionOutcome.KeepOpen, await box.Model.ExecuteAsync(Gesture.Enter));
            Assert.True(box.Model.IsConfirming);
            Assert.Empty(_ran);
        });
    }

    [Fact]
    public void A_pick_is_remembered_for_what_was_typed_even_when_the_box_resets_meanwhile()
    {
        Ui.Run(async () =>
        {
            // The app being started takes the foreground, the box loses it and resets, and only
            // then does the launch return.
            var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notepad = Row("Notepad", run: async () =>
            {
                await launched.Task;
                return ActionOutcome.Hide;
            });
            var history = new UsageHistory();
            using var box = new Box([Register("apps", Remembered(_ => [notepad]))], history);
            await box.Model.SearchAsync("note");

            var enter = box.Model.ExecuteAsync(Gesture.Enter);
            box.Model.Reset();
            launched.SetResult();
            await enter;

            var use = Assert.Single(Assert.Single(history.ToSnapshot().Entries).Uses);
            Assert.Equal("note", use.Query);
        });
    }

    [Fact]
    public void Only_a_remembering_providers_picks_reach_history()
    {
        Ui.Run(async () =>
        {
            var history = new UsageHistory();
            using var box = new Box([Register("web", Rows(_ => [Row("Search Google for my secret")]))], history);
            await box.Model.SearchAsync("my secret");

            await box.Model.ExecuteAsync(Gesture.Enter);

            Assert.Equal(0, history.Count);
        });
    }

    [Fact]
    public void An_action_that_never_finishes_does_not_leave_enter_dead()
    {
        Ui.Run(async () =>
        {
            var stuck = Row("Stuck", run: async () =>
            {
                await Task.Delay(Timeout.Infinite, CancellationToken.None);
                return ActionOutcome.Hide;
            });
            var fine = Row("Fine");
            using var box = new Box(
                [Register("apps", Rows(q => q.Search == "stuck" ? [stuck] : [fine]))],
                actionBudget: TimeSpan.FromMilliseconds(200));

            await box.Model.SearchAsync("stuck");
            Assert.Equal(ActionOutcome.KeepOpen, await box.Model.ExecuteAsync(Gesture.Enter));
            Assert.Contains("taking too long", box.Model.Status, StringComparison.Ordinal);

            await box.Model.SearchAsync("fine");
            Assert.Equal(ActionOutcome.Hide, await box.Model.ExecuteAsync(Gesture.Enter));
            Assert.Equal(["Fine"], _ran);
        });
    }

    [Fact]
    public void A_last_resort_waits_for_every_provider_and_never_takes_the_top()
    {
        Ui.Run(async () =>
        {
            using var box = new Box(
                Register("files", Rows(_ => [Row("report.pdf")]), FilesDebounce),
                Register("web", Rows(q => [Row($"Search Google for {q.Search}", Result.LastResort / 2)])));

            await box.Model.SearchAsync("report");

            Assert.Equal(["report.pdf", "Search Google for report"], box.Model.Rows.Select(r => r.Title));
        });
    }

    [Fact]
    public void A_failure_is_said_in_the_box_and_its_message_is_kept_out_of_the_log()
    {
        Ui.Run(async () =>
        {
            var broken = Row("Open", run: () => throw new InvalidOperationException(@"could not open C:\Users\me\my secret plan.docx"));
            using var box = new Box([Register("files", Rows(_ => [broken]))]);
            await box.Model.SearchAsync("plan");

            Assert.Equal(ActionOutcome.KeepOpen, await box.Model.ExecuteAsync(Gesture.Enter));

            Assert.Contains("did not work", box.Model.Status, StringComparison.Ordinal);
            Assert.DoesNotContain(box.Log.Lines, l => l.Contains("secret", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Replacing_the_providers_lets_go_of_the_old_sets_rows_and_question_and_reads_the_new_keywords()
    {
        Ui.Run(async () =>
        {
            var confirmed = new ResultAction("Restart", Gesture.Enter, (_, _) => Ran("restart"));
            var restart = Row("Restart", run: () => ValueTask.FromResult(ActionOutcome.AskFirst("Restart?", confirmed)));
            using var box = new Box([Register("old", Rows(_ => [restart]))]);
            await box.Model.SearchAsync("re");
            await box.Model.ExecuteAsync(Gesture.Enter);
            Assert.True(box.Model.IsConfirming);

            // Reload plugins: a plugin's rows and questions must not outlive its set, or it cannot be unloaded.
            box.Supervisor.Replace([box.Registration("new", Rows(q => [Row($"new {q.Search}")]), keyword: "k")]);
            box.Model.ProvidersReplaced();

            Assert.False(box.Model.IsConfirming);
            Assert.Empty(box.Model.Rows);

            await box.Model.SearchAsync("k cats");
            Assert.Equal("new", box.Model.Scope);
            Assert.Equal(["new cats"], box.Model.Rows.Select(r => r.Title));
            Assert.Empty(_ran);
        });
    }

    [Fact]
    public void A_finished_keyword_becomes_a_chip_and_what_follows_is_searched_behind_it()
    {
        Ui.Run(async () =>
        {
            using var box = WithGoogle();

            Assert.False(box.Model.TryTakeChip("g", out _));
            Assert.True(box.Model.TryTakeChip("g ", out var rest));
            Assert.Equal(string.Empty, rest);
            Assert.Equal("Google", box.Model.Scope);
            Assert.Equal("g", box.Model.Chip?.Keyword);

            await box.Model.SearchAsync("cats");
            Assert.Equal(["web cats"], box.Model.Rows.Select(r => r.Title));

            // One chip at a time: "g dogs" typed behind Google is a Google search for it.
            Assert.False(box.Model.TryTakeChip("g dogs", out _));
            await box.Model.SearchAsync("g dogs");
            Assert.Equal(["web g dogs"], box.Model.Rows.Select(r => r.Title));
        });
    }

    [Fact]
    public void Removing_the_chip_searches_the_text_everywhere_again()
    {
        Ui.Run(async () =>
        {
            using var box = WithGoogle();
            Assert.True(box.Model.TryTakeChip("g cats", out var rest));
            Assert.Equal("cats", rest);

            Assert.True(box.Model.RemoveChip());
            await box.Model.SearchAsync(rest);

            Assert.Null(box.Model.Scope);
            Assert.Contains("app cats", box.Model.Rows.Select(r => r.Title));
            Assert.False(box.Model.RemoveChip());
        });
    }

    [Fact]
    public void The_chip_goes_when_the_box_resets_and_stays_over_new_providers_that_answer_to_it()
    {
        Ui.Run(async () =>
        {
            using var box = WithGoogle();
            box.Model.TryTakeChip("g ", out _);

            box.Model.ProvidersReplaced();
            Assert.Equal("Google", box.Model.Scope);
            await box.Model.SearchAsync("cats");
            Assert.Equal(["web cats"], box.Model.Rows.Select(r => r.Title));

            box.Supervisor.Replace([box.Registration("apps", Rows(q => [Row($"app {q.Search}")]))]);
            box.Model.ProvidersReplaced();
            Assert.Null(box.Model.Chip);

            box.Model.Reset();
            Assert.Null(box.Model.Scope);
        });
    }

    private Box WithGoogle()
    {
        var box = new Box();
        box.Supervisor.Replace(
        [
            box.Registration("apps", Rows(q => [Row($"app {q.Search}")])),
            box.Registration("web", Rows(q => [Row($"web {q.Search}")]), keyword: "g") with
            {
                Keywords = [new ProviderKeyword("g", "Google")],
                IsGlobal = false,
            },
        ]);
        box.Model.ProvidersReplaced();
        return box;
    }

    private ValueTask<ActionOutcome> Ran(string what)
    {
        lock (_ran)
        {
            _ran.Add(what);
        }

        return ValueTask.FromResult(ActionOutcome.Hide);
    }

    private Result Row(string title, double relevance = 0.9, Func<ValueTask<ActionOutcome>>? run = null) =>
        new(
            title,
            title,
            Subtitle: null,
            IconSource.None,
            relevance,
            new ResultAction("Open", Gesture.Enter, (_, _) => run is null ? Ran(title) : run()),
            Secondary: [],
            Highlight: null);

    private static ListProvider Rows(Func<Query, IReadOnlyList<Result>> rows) => new(rows);

    private static RememberingProvider Remembered(Func<Query, IReadOnlyList<Result>> rows) => new(rows);

    private static (string Id, IProvider Provider, TimeSpan Debounce) Register(string id, IProvider provider, TimeSpan debounce = default) =>
        (id, provider, debounce);

    /// <summary>A view model with its supervisor and history, over the given providers.</summary>
    private sealed class Box : IDisposable
    {
        private readonly StaWorker _worker = new("view model tests");

        public Box(params (string Id, IProvider Provider, TimeSpan Debounce)[] providers)
            : this(providers, history: null)
        {
        }

        public Box(
            IReadOnlyList<(string Id, IProvider Provider, TimeSpan Debounce)> providers,
            UsageHistory? history = null,
            TimeProvider? time = null,
            TimeSpan? actionBudget = null)
        {
            var registrations = providers.Select(p => Registration(p.Id, p.Provider, p.Debounce)).ToArray();

            Supervisor = new Supervisor(registrations, Log, new SupervisorOptions { FirstFrameBudget = TimeSpan.FromMilliseconds(100) });
            Model = new SearchViewModel(Supervisor, history ?? new UsageHistory(), new IconCache(new ShellIcons(_worker)), Log, time)
            {
                ActionBudget = actionBudget ?? TimeSpan.FromSeconds(20),
            };
        }

        public RecordingLog Log { get; } = new();

        public Supervisor Supervisor { get; }

        public ProviderRegistration Registration(string id, IProvider provider, TimeSpan debounce = default, string? keyword = null) =>
            new(id, id, provider, new PluginContext(id, new NullLauncher(), new FuzzyMatcher(), new NullClipboard(), Log))
            {
                Debounce = debounce,
                Keywords = keyword is null ? [] : [new ProviderKeyword(keyword, id)],
            };

        public SearchViewModel Model { get; }

        public void Dispose() => _worker.Dispose();
    }

    private class ListProvider(Func<Query, IReadOnlyList<Result>> rows) : IProvider
    {
        public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<Result> QueryAsync(Query query, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var row in rows(query))
            {
                ct.ThrowIfCancellationRequested();
                yield return row;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class RememberingProvider(Func<Query, IReadOnlyList<Result>> rows) : ListProvider(rows), IRecall
    {
        public ValueTask<Recollection> RecallAsync(string id, CancellationToken ct) => ValueTask.FromResult(Recollection.Unknown);
    }

    private sealed class NullLauncher : ILauncher
    {
        public ValueTask LaunchAppAsync(string appId, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask LaunchAppAsAdministratorAsync(string appId, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask OpenUriAsync(Uri uri, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask OpenPathAsync(string path, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask ShowInFolderAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NullClipboard : IClipboard
    {
        public ValueTask SetTextAsync(string text, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask SetFileAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    }
}

internal sealed class RecordingLog : IHostLog
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public void LogInfo(string message)
    {
        lock (_lines)
        {
            _lines.Add(message);
        }
    }

    public void LogError(string message, Exception? exception = null)
    {
        lock (_lines)
        {
            _lines.Add(message + (exception is null ? string.Empty : " " + exception));
        }
    }
}

/// <summary>A clock whose time a test moves; its timers are the real ones.</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero).UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>Runs a test body on a WPF dispatcher thread, the thread the view model lives on.</summary>
internal static class Ui
{
    public static void Run(Func<Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    frame.Continue = false;
                }
            });
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("The test did not finish on its dispatcher thread.");
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }
}
