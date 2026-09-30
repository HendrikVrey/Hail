using Hail.Core.History;

namespace Hail.Persistence.Tests;

public sealed class UsageStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly UsageKey Notepad = new("hail.apps", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "HailTests", Guid.NewGuid().ToString("N"));
    private readonly HailPaths _paths;

    public UsageStoreTests() => _paths = new HailPaths(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void History_survives_a_restart()
    {
        var history = new UsageHistory();
        history.Record("note", Notepad, Now);
        new UsageStore(_paths).Save(history.ToSnapshot());

        var loaded = new UsageStore(_paths).Load(Now, out var problem);

        Assert.Null(problem);
        Assert.Equal([Notepad], loaded.Top(8, Now));
        Assert.True(loaded.Lift("no", Notepad, Now) > 0);
    }

    [Fact]
    public void No_file_is_no_history_and_no_problem()
    {
        var loaded = new UsageStore(_paths).Load(Now, out var problem);

        Assert.Null(problem);
        Assert.Equal(0, loaded.Count);
    }

    [Fact]
    public void An_unreadable_file_is_set_aside_not_lost()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_paths.Usage, "{ this is not json");

        var loaded = new UsageStore(_paths).Load(Now, out var problem);

        Assert.Equal(0, loaded.Count);
        Assert.NotNull(problem);
        Assert.False(File.Exists(_paths.Usage));
        Assert.Equal("{ this is not json", File.ReadAllText(Path.Combine(_root, "usage.unreadable.json")));
    }

    [Fact]
    public void A_newer_hails_file_is_neither_read_nor_overwritten()
    {
        Directory.CreateDirectory(_root);
        const string newer = """{ "version": 99, "entries": [] }""";
        File.WriteAllText(_paths.Usage, newer);
        var store = new UsageStore(_paths);

        store.Load(Now, out var problem);
        var history = new UsageHistory();
        history.Record("x", Notepad, Now);
        store.Save(history.ToSnapshot());

        Assert.Contains("newer", problem, StringComparison.Ordinal);
        Assert.Equal(newer, File.ReadAllText(_paths.Usage));
    }

    [Fact]
    public void What_is_written_holds_no_more_of_a_query_than_its_start()
    {
        var history = new UsageHistory();
        history.Record("my bank account password is hunter2 and more", Notepad, Now);
        new UsageStore(_paths).Save(history.ToSnapshot());

        var text = File.ReadAllText(_paths.Usage);

        Assert.DoesNotContain("password", text, StringComparison.Ordinal);
        Assert.Contains("\"my bank account\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_atomic_write_leaves_no_temporary_file()
    {
        AtomicFile.WriteAllText(Path.Combine(_root, "a.json"), "one");
        AtomicFile.WriteAllText(Path.Combine(_root, "a.json"), "two");

        Assert.Equal("two", File.ReadAllText(Path.Combine(_root, "a.json")));
        Assert.Equal(["a.json"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }
}
