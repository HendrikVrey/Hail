using Hail.Core.Settings;
using Hail.Core.Updates;
using Hail.Plugins;
using Hail.Windows.Startup;

namespace Hail.App;

/// <summary>A built-in provider as the settings window lists it.</summary>
/// <param name="Keywords">What is typed first to ask it alone, if anything.</param>
internal sealed record BuiltInProvider(string Id, string Name, string Description, IReadOnlyList<string> Keywords);

/// <summary>The sections of the settings window, in the order it lists them.</summary>
internal enum SettingsSection
{
    General,
    Providers,
    WebSearch,
    Plugins,
    History,
    Updates,
    About,
}

/// <summary>
/// What the settings window asks of the host (Hail.md §10.4). Every change is applied at once and
/// saved; each method that can fail answers with a sentence for the window, or null.
/// </summary>
internal interface ISettingsHost
{
    /// <summary>The plugins were read again, or an answer about one was recorded.</summary>
    event Action? PluginsChanged;

    HailSettings Settings { get; }

    string Version { get; }

    /// <summary>Whether the shortcut in <see cref="Settings"/> is registered with Windows now.</summary>
    bool HotkeyRegistered { get; }

    IReadOnlyList<BuiltInProvider> BuiltInProviders { get; }

    /// <summary>Keywords the web engines may not take, each with its owner's name.</summary>
    IReadOnlyDictionary<string, string> KeywordsOutsideWebSearch { get; }

    IReadOnlyList<PluginEntry> Plugins { get; }

    int HistoryCount { get; }

    UpdateCoordinator Updates { get; }

    /// <summary>Saves <paramref name="next"/> and puts it in force.</summary>
    string? Apply(HailSettings next);

    /// <summary>Lets go of the shortcut while a new one is recorded, so pressing it reaches the recorder.</summary>
    void SuspendHotkey();

    /// <summary>Registers <paramref name="chord"/> and saves it; nothing is registered when it fails.</summary>
    string? TryHotkey(Chord chord);

    /// <summary>Takes the saved shortcut back after a recording that ended without one.</summary>
    void ResumeHotkey();

    StartupState StartupState { get; }

    string? SetStartup(bool enabled);

    string DescribePlugin(PluginEntry entry);

    bool CanRecordPluginAnswers { get; }

    void AskAboutPlugin(PluginEntry entry);

    void SwitchOffPlugin(PluginEntry entry);

    void EditPluginSettings(PluginEntry entry);

    void ReloadPlugins();

    void OpenPluginsFolder();

    void ClearHistory();

    /// <summary>Downloads and starts the offered update; Hail quits when the installer is running.</summary>
    Task InstallUpdateAsync();

    /// <summary>Opens <c>%LOCALAPPDATA%\Hail</c>: settings, history, logs and plugins.</summary>
    void OpenDataFolder();

    void OpenReleasesPage();

    void OpenProjectPage();
}
