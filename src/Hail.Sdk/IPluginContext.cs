namespace Hail.Sdk;

/// <summary>
/// Everything the host gives a provider. A provider needs nothing else, and a well-behaved
/// one reaches for nothing else: launching goes through <see cref="Launcher"/> so the host's
/// rules apply to it (Hail.md §9).
/// </summary>
/// <remarks>
/// SDK 0.2 carries what the four built-in providers need. Settings, a data folder and read
/// access to history arrive with plugins (M2); the contract grows in public rather than being
/// guessed at ahead of use.
/// </remarks>
public interface IPluginContext
{
    ILauncher Launcher { get; }

    /// <summary>
    /// The host's fuzzy matcher, offered so that every provider scores text the same way and
    /// a stranger's results rank fairly against the built-in ones.
    /// </summary>
    IMatcher Matcher { get; }

    IClipboard Clipboard { get; }

    IPluginLog Log { get; }
}

/// <summary>
/// Starts and opens things on the provider's behalf, under the host's rules. A provider never
/// needs to start a process itself, and a well-behaved one never does.
/// </summary>
/// <remarks>
/// Each method refuses what the host would not do by throwing
/// <see cref="LaunchRefusedException"/>, whose message is a sentence the host shows the user.
/// </remarks>
public interface ILauncher
{
    /// <summary>
    /// Starts the app whose identity is <paramref name="appId"/>: an AppUserModelID or the
    /// shell's parsing name for a Start menu entry. The host refuses an id it did not itself
    /// list, so a provider cannot turn typed text into a started program.
    /// </summary>
    ValueTask LaunchAppAsync(string appId, CancellationToken ct);

    /// <summary>
    /// As <see cref="LaunchAppAsync"/>, elevated: Windows asks the user first. Declining that
    /// question is not an error and returns normally.
    /// </summary>
    ValueTask LaunchAppAsAdministratorAsync(string appId, CancellationToken ct);

    /// <summary>Opens a web address in the default browser. Only http and https are opened.</summary>
    ValueTask OpenUriAsync(Uri uri, CancellationToken ct);

    /// <summary>
    /// Opens a file or folder with whatever Windows opens it with. The path must be a full
    /// path on a local drive that exists; network paths are refused.
    /// </summary>
    ValueTask OpenPathAsync(string path, CancellationToken ct);

    /// <summary>Opens the folder that holds <paramref name="path"/> with it selected.</summary>
    ValueTask ShowInFolderAsync(string path, CancellationToken ct);
}

/// <summary>Puts things on the Windows clipboard.</summary>
public interface IClipboard
{
    ValueTask SetTextAsync(string text, CancellationToken ct);

    /// <summary>
    /// Puts the file itself on the clipboard, as Explorer's Copy does, so it can be pasted into
    /// a folder or an email. The same rules as <see cref="ILauncher.OpenPathAsync"/> apply.
    /// </summary>
    ValueTask SetFileAsync(string path, CancellationToken ct);
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
