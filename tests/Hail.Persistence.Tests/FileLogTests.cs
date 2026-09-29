using System.Globalization;

namespace Hail.Persistence.Tests;

public sealed class FileLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HailTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_line_is_on_disk_as_soon_as_it_is_written()
    {
        var now = new DateTime(2026, 9, 29, 14, 5, 6, 789);
        var log = new FileLog(new HailPaths(_root), () => now);

        log.LogInfo("Hail starting.");
        log.LogError("It broke.", new InvalidOperationException("because"));

        var text = File.ReadAllText(Path.Combine(_root, "logs", "hail-2026-09-29.log"));
        Assert.Contains("2026-09-29 14:05:06.789 INFO  Hail starting.", text, StringComparison.Ordinal);
        Assert.Contains("ERROR It broke.", text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: because", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Lines_are_written_the_same_whatever_the_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // This machine's culture writes 20,0 and a different date order.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("af-ZA");
            var log = new FileLog(new HailPaths(_root), () => new DateTime(2026, 1, 2, 3, 4, 5, 6));
            log.LogInfo("x");

            Assert.StartsWith("2026-01-02 03:04:05.006", File.ReadAllText(log.PathFor(new DateTime(2026, 1, 2))), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Pruning_keeps_thirty_days_and_nothing_that_is_not_a_log()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_root, "logs")).FullName;
        foreach (var name in new[] { "hail-2026-08-29.log", "hail-2026-08-30.log", "hail-2026-09-29.log", "hail-notadate.log", "notes.txt" })
        {
            File.WriteAllText(Path.Combine(logs, name), "x");
        }

        new FileLog(new HailPaths(_root), () => new DateTime(2026, 9, 29)).Prune();

        Assert.Equal(
            ["hail-2026-08-30.log", "hail-2026-09-29.log", "hail-notadate.log", "notes.txt"],
            Directory.EnumerateFiles(logs).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Pruning_a_folder_that_does_not_exist_yet_is_nothing()
    {
        new FileLog(new HailPaths(_root)).Prune();
        Assert.False(Directory.Exists(_root));
    }
}
