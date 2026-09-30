using Hail.Core.Ports;
using Hail.Sdk;

namespace Hail.Core.Hosting;

/// <summary>What the host hands one provider.</summary>
public sealed class PluginContext(string pluginId, ILauncher launcher, IMatcher matcher, IClipboard clipboard, IHostLog log) : IPluginContext
{
    public ILauncher Launcher { get; } = launcher;

    public IMatcher Matcher { get; } = matcher;

    public IClipboard Clipboard { get; } = clipboard;

    public IPluginLog Log { get; } = new PluginLog(pluginId, log);
}

/// <summary>A plugin's view of the host log: every line carries the plugin's id.</summary>
public sealed class PluginLog(string pluginId, IHostLog log) : IPluginLog
{
    public void LogInfo(string message) => log.LogInfo($"[{pluginId}] {message}");

    public void LogError(string message, Exception? exception = null) => log.LogError($"[{pluginId}] {message}", exception);
}

/// <summary>What an action is told when it runs.</summary>
public sealed record ActionContext(Query Query) : IActionContext;
