namespace Hail.Sdk;

/// <summary>
/// A source of results. Everything Hail can find comes from one of these, the built-in apps
/// provider included, which is written against this interface exactly as a stranger's plugin
/// would be.
/// </summary>
public interface IProvider
{
    /// <summary>
    /// Called once, lazily, before the first query that reaches this provider. Keep it cheap:
    /// the user is waiting on the query that caused it.
    /// </summary>
    ValueTask InitializeAsync(IPluginContext context, CancellationToken ct);

    /// <summary>
    /// Streams results as they are found; the host merges and ranks them. The token is
    /// cancelled on the next keystroke, and a provider that ignores it is cut off rather than
    /// waited for, so honour it.
    /// </summary>
    IAsyncEnumerable<Result> QueryAsync(Query query, CancellationToken ct);
}

/// <summary>
/// A provider whose results are worth remembering: an app, a file. The host records which of
/// them the user picks and for what they had typed, ranks those higher next time, and shows
/// the usual ones in an empty box by asking the provider to rebuild them from their ids.
/// </summary>
/// <remarks>
/// A provider that does not implement this has nothing it picks remembered at all. That is
/// deliberate for anything whose result id carries what the user typed (a web search, a sum):
/// history is written to disk, and what was typed must not be.
/// </remarks>
public interface IRecall
{
    /// <summary>
    /// False while the provider cannot yet tell what exists (before it has read its catalog
    /// for the first time, say). The host then asks for nothing, and forgets nothing.
    /// </summary>
    bool CanRecall { get; }

    /// <summary>
    /// The result <paramref name="id"/> names, as a query would produce it now; null when it
    /// no longer exists (an app uninstalled, a file deleted), and the host then forgets it.
    /// Called with an empty <see cref="Query"/>; keep it quick.
    /// </summary>
    ValueTask<Result?> RecallAsync(string id, CancellationToken ct);
}
