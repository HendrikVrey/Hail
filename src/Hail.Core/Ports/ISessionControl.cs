namespace Hail.Core.Ports;

/// <summary>
/// Windows' own session commands, for Hail's commands provider (Hail.md §7.5).
/// <c>Hail.Windows</c> implements it. Each throws when Windows refuses, with the reason.
/// </summary>
public interface ISessionControl
{
    void Lock();

    void Sleep();

    void SignOut();

    void Restart();

    void ShutDown();
}

/// <summary>What Hail itself can be asked to do from the box.</summary>
public interface IHostCommands
{
    /// <summary>Opens Hail's settings window. Returns at once: it opens after the box has hidden.</summary>
    void OpenSettings();

    /// <summary>Where plugins are installed, one folder each; it exists once Hail has started.</summary>
    string PluginsFolder { get; }

    /// <summary>Reads the plugins folder again and loads its plugins afresh (Hail.md §6.3). Returns at once.</summary>
    void ReloadPlugins();

    /// <summary>
    /// Ends Hail. Returns at once: the host quits after the action that asked has finished
    /// and the box has hidden, so nothing is torn down under the caller.
    /// </summary>
    void Quit();
}
