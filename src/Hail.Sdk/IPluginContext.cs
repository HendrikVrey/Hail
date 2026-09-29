namespace Hail.Sdk;

/// <summary>
/// Everything the host gives a provider. A provider needs nothing else, and a well-behaved
/// one reaches for nothing else: launching goes through <see cref="Launcher"/> so the host's
/// rules apply to it (Hail.md §9).
/// </summary>
/// <remarks>
/// SDK 0.1 carries what the apps provider needs. Settings, the clipboard, a data folder and
/// read access to history arrive with the providers that need them (M1, M2); the contract
/// grows in public rather than being guessed at ahead of use.
/// </remarks>
public interface IPluginContext
{
    ILauncher Launcher { get; }

    /// <summary>
    /// The host's fuzzy matcher, offered so that every provider scores text the same way and
    /// a stranger's results rank fairly against the built-in ones.
    /// </summary>
    IMatcher Matcher { get; }

    IPluginLog Log { get; }
}

/// <summary>Starts things on the provider's behalf, under the host's rules.</summary>
public interface ILauncher
{
    /// <summary>
    /// Starts the app whose identity is <paramref name="appId"/>: an AppUserModelID or the
    /// shell's parsing name for a Start menu entry. The host refuses an id it did not itself
    /// list, so a provider cannot turn typed text into a started program.
    /// </summary>
    ValueTask LaunchAppAsync(string appId, CancellationToken ct);
}

/// <summary>Scores how well a query matches a piece of text.</summary>
public interface IMatcher
{
    /// <summary>
    /// Null when <paramref name="query"/> does not match <paramref name="candidate"/> at all;
    /// otherwise a score between 0 and 1 and the characters that matched, for bolding.
    /// </summary>
    MatchResult? Match(string query, string candidate);
}

/// <summary>Writes into Hail's log, prefixed with the plugin's id.</summary>
/// <remarks>Never log what the user typed; the host's log never does (Hail.md §9).</remarks>
public interface IPluginLog
{
    void LogInfo(string message);

    void LogError(string message, Exception? exception = null);
}
