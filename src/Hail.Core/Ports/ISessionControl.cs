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
    /// <summary>Hail's settings file, which the box offers to open until M3 brings a settings window.</summary>
    string SettingsPath { get; }

    /// <summary>
    /// Ends Hail. Returns at once: the host quits after the action that asked has finished
    /// and the box has hidden, so nothing is torn down under the caller.
    /// </summary>
    void Quit();
}
