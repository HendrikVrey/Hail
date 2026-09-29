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
