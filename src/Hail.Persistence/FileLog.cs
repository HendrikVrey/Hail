using System.Globalization;
using System.Text;
using Hail.Core.Ports;

namespace Hail.Persistence;

/// <summary>
/// One log file a day in <see cref="HailPaths.Logs"/>, the last <see cref="DaysKept"/> kept.
/// Written in the invariant culture, so a line reads the same on every machine.
/// </summary>
/// <remarks>
/// Each line is appended and flushed as it is written: the log exists to explain what
/// happened before a crash, which a buffered writer loses. A launcher logs a handful of lines
/// an hour, so the cost does not matter. A write that fails is dropped rather than thrown:
/// the log must never be the reason the box does not appear.
/// </remarks>
public sealed class FileLog : IHostLog
{
    public const int DaysKept = 30;

    private readonly string _folder;
    private readonly Func<DateTime> _now;
    private readonly Lock _gate = new();

    public FileLog(HailPaths paths, Func<DateTime>? now = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _folder = paths.Logs;
        _now = now ?? (() => DateTime.Now);
    }

    public void LogInfo(string message) => Write("INFO ", message, exception: null);

    public void LogError(string message, Exception? exception = null) => Write("ERROR", message, exception);

    /// <summary>Deletes log files older than <see cref="DaysKept"/> days. Called once at startup.</summary>
    public void Prune()
    {
        if (!Directory.Exists(_folder))
        {
            return;
        }

        var oldest = DateOnly.FromDateTime(_now()).AddDays(-DaysKept);
        foreach (var file in Directory.EnumerateFiles(_folder, "hail-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)["hail-".Length..];
            if (DateOnly.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day < oldest)
            {
                TryDelete(file);
            }
        }
    }

    public string PathFor(DateTime day) =>
        Path.Combine(_folder, string.Create(CultureInfo.InvariantCulture, $"hail-{day:yyyy-MM-dd}.log"));

    private void Write(string level, string message, Exception? exception)
    {
        var now = _now();
        var line = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"{now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}");
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        line.AppendLine();

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                File.AppendAllText(PathFor(now), line.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Dropped on purpose: the log is the place failures are reported, so there is
                // nowhere left to report its own, and throwing would take the box down with it.
            }
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An old log that cannot be deleted today (open in an editor, say) is tried again
            // at the next start; it is not worth a line in the log it belongs to.
        }
    }
}
