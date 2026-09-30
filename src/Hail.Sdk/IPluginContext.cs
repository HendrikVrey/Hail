namespace Hail.Sdk;

/// <summary>
/// Everything the host gives a provider. A provider needs nothing else, and a well-behaved
/// one reaches for nothing else: launching goes through <see cref="Launcher"/> so the host's
/// rules apply to it (Hail.md §9).
/// </summary>
public interface IPluginContext
{
    /// <summary>Starts apps and opens addresses, files and folders, under the host's rules.</summary>
    ILauncher Launcher { get; }

    /// <summary>
    /// The host's fuzzy matcher, offered so that every provider scores text the same way and
    /// a stranger's results rank fairly against the built-in ones.
    /// </summary>
    IMatcher Matcher { get; }

    /// <summary>Puts text or a file on the Windows clipboard.</summary>
    IClipboard Clipboard { get; }

    /// <summary>Writes into Hail's log, each line marked with the provider's id.</summary>
    IPluginLog Log { get; }

    /// <summary>
    /// The values of the settings the plugin declares in its manifest, as the user has set
    /// them. They can change while Hail runs, so read them when they are needed rather than
    /// once at start.
    /// </summary>
    IPluginSettings Settings { get; }

    /// <summary>
    /// What the user picks from this provider most, for a provider that implements
    /// <see cref="IRecall"/>; empty for one that does not, because nothing it offers is
    /// remembered.
    /// </summary>
    IPluginHistory History { get; }

    /// <summary>
    /// A folder of the provider's own under the user's profile, for a cache or a small
    /// database. The host creates it the first time this is read. Nothing else writes there,
    /// and deleting it loses only what the provider kept.
    /// </summary>
    string DataFolder { get; }
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
    /// <summary>Puts <paramref name="text"/> on the clipboard as plain text.</summary>
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
    /// <summary>Records something that happened.</summary>
    void LogInfo(string message);

    /// <summary>Records a failure. The exception's type and stack are kept, never its message.</summary>
    void LogError(string message, Exception? exception = null);
}

/// <summary>
/// The settings a plugin declared in its manifest's <c>settings</c> list, as the user has set
/// them in Hail. Each is read by its key, with the method for its kind.
/// </summary>
/// <remarks>
/// Asking for a key the manifest does not declare, or with the method for another kind,
/// throws <see cref="ArgumentException"/>: that is a mistake in the plugin, and saying so
/// at once is kinder than a default that hides it.
/// </remarks>
public interface IPluginSettings
{
    /// <summary>A <c>text</c> setting: what the user typed, or the manifest's default.</summary>
    string GetText(string key);

    /// <summary>A <c>choice</c> setting: one of the values the manifest lists.</summary>
    string GetChoice(string key);

    /// <summary>A <c>toggle</c> setting.</summary>
    bool GetToggle(string key);

    /// <summary>
    /// A <c>secret</c> setting (a token, a password), or null when the user has not set one.
    /// The host keeps it encrypted for the Windows account and never shows it again once
    /// entered. Do not log it.
    /// </summary>
    string? GetSecret(string key);
}

/// <summary>Read access to what the user picks from this provider.</summary>
public interface IPluginHistory
{
    /// <summary>
    /// The ids of this provider's results the user picks most, most first, weighed by how
    /// often and how recently: what to offer when the provider's keyword is typed with nothing
    /// after it. Rebuild each with <see cref="IRecall.RecallAsync"/>'s own logic.
    /// </summary>
    IReadOnlyList<string> MostPicked(int count);
}
