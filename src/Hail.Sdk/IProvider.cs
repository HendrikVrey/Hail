namespace Hail.Sdk;

/// <summary>
/// A source of results. Everything Hail can find comes from one of these, the built-in apps
/// provider included, which is written against this interface exactly as a stranger's plugin
/// would be.
/// </summary>
/// <remarks>
/// <para>
/// Threading, which a provider must be written for: every method is called on a background
/// thread, never the UI thread. <see cref="QueryAsync"/> may be called again before an earlier
/// call has finished (the earlier one cancelled, or abandoned past its budget and still
/// running), and at the same time as <see cref="IRecall.RecallAsync"/>, which is itself called
/// for several ids at once. Keep state read-only after initialisation, or lock it.
/// </para>
/// <para>
/// A result's actions (<see cref="ResultAction.Execute"/>) also run on a background thread,
/// with a token that is cancelled when the host stops waiting for them.
/// </para>
/// </remarks>
public interface IProvider
{
    /// <summary>
    /// Called once, lazily, before the first query that reaches this provider. Keep it cheap:
    /// the user is waiting on the query that caused it. The token is never cancelled, because a
    /// keystroke must not leave a provider half initialised; a query stops waiting for it after
    /// its budget and the initialisation carries on.
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
    /// Rebuilds the result <paramref name="id"/> names, as a query would produce it now.
    /// Answer <see cref="Recollection.Gone"/> only when it is known not to exist any more (an
    /// app uninstalled, a file deleted from a drive that is there): the host then forgets it
    /// for good. When it cannot be told (the catalog not read yet, a USB stick unplugged),
    /// answer <see cref="Recollection.Unknown"/>, and nothing is shown or forgotten. Keep it
    /// quick: an empty box is waiting.
    /// </summary>
    ValueTask<Recollection> RecallAsync(string id, CancellationToken ct);
}

/// <summary>What a provider could say about a remembered result.</summary>
public abstract record Recollection
{
    private Recollection()
    {
    }

    /// <summary>It no longer exists; the host forgets it.</summary>
    public static Recollection Gone { get; } = new GoneRecollection();

    /// <summary>It cannot be told now; the host shows nothing and forgets nothing.</summary>
    public static Recollection Unknown { get; } = new UnknownRecollection();

    public static Recollection Of(Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new Found(result);
    }

    public sealed record Found(Result Result) : Recollection;

    public sealed record GoneRecollection : Recollection;

    public sealed record UnknownRecollection : Recollection;
}
