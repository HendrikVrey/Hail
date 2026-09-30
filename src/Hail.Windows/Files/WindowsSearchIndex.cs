using System.Data.OleDb;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Hail.Core.Ports;
using Hail.Core.Search;
using Hail.Windows.Launching;

namespace Hail.Windows.Files;

/// <summary>
/// Asks the Windows Search index for files by name, over OLE DB (Hail.md §7.2). Hail keeps no
/// index of its own; this finds what the user has told Windows to index, and nothing else.
/// </summary>
/// <remarks>
/// A connection per query: opening one costs little beside the query, and holding one open
/// would hold a session in the search service for as long as Hail runs. When the service is
/// off or refuses, the answer is <see cref="FileIndexAnswer.Unavailable"/>, never an exception.
/// </remarks>
public sealed class WindowsSearchIndex : IFileIndex
{
    private const string ConnectionString = "Provider=Search.CollatorDSO;Extended Properties=\"Application=Windows\"";

    private const string NotAnswering = "Windows Search is not answering, so Hail cannot find files by name.";

    public Task<FileIndexAnswer> SearchAsync(string search, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(search);

        var sql = WindowsSearchSql.FileNameQuery(search, limit);
        if (sql is null)
        {
            return Task.FromResult<FileIndexAnswer>(new FileIndexAnswer.Found([]));
        }

        return Task.Run(() => Run(sql, ct), ct);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The index's dialect takes no parameters; WindowsSearchSql builds the text and is the only way a typed string reaches it, with its escaping tested against injection.")]
    private static FileIndexAnswer Run(string sql, CancellationToken ct)
    {
        try
        {
            using var connection = new OleDbConnection(ConnectionString);
            connection.Open();
            using var command = new OleDbCommand(sql, connection);
            // Cancelling crosses into the search service; a keystroke cancels on the UI thread,
            // so the call is sent to the pool rather than made in the callback.
            using var cancel = ct.Register(() => ThreadPool.QueueUserWorkItem(_ => TryCancel(command)));
            using var reader = command.ExecuteReader();

            var items = new List<LocalItem>();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();

                // Columns in WindowsSearchSql's order: path, name, type. The index is data too;
                // a row that is not a local path is skipped rather than trusted.
                if (reader.GetValue(0) is string path && LocalPaths.IsAcceptable(path)
                    && reader.GetValue(1) is string name && name.Length > 0
                    && IsShown(path))
                {
                    var isFolder = reader.GetValue(2) is string type && string.Equals(type, WindowsSearchSql.FolderType, StringComparison.OrdinalIgnoreCase);
                    items.Add(new LocalItem(path, name, isFolder));
                }
            }

            return new FileIndexAnswer.Found(items);
        }
        catch (OleDbException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex) when (ex is OleDbException or COMException or InvalidOperationException)
        {
            return new FileIndexAnswer.Unavailable(NotAnswering);
        }
    }

    private static void TryCancel(OleDbCommand command)
    {
        try
        {
            command.Cancel();
        }
        catch (Exception ex) when (ex is OleDbException or InvalidOperationException or ObjectDisposedException)
        {
            // The query finished, or the connection closed, before the cancel arrived.
        }
    }

    /// <summary>
    /// Whether the item is on the disk now and is one Explorer shows. The index lags the disk
    /// (it has rows for files deleted since) and its attributes with it, so the disk is asked,
    /// once per row: hidden and system files (desktop.ini) are left out, as Explorer leaves them.
    /// </summary>
    private static bool IsShown(string path)
    {
        try
        {
            return (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
