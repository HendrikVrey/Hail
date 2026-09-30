using Hail.Core.History;
using Hail.Core.Plugins;
using Hail.Core.Ports;
using Hail.Sdk;

namespace Hail.Core.Hosting;

/// <summary>What the host hands one provider.</summary>
public sealed class PluginContext : IPluginContext
{
    private readonly Lazy<string> _dataFolder;

    /// <param name="dataFolder">
    /// Makes the provider's data folder and returns its path; called the first time a provider
    /// asks, so nothing is created on disk for one that never does.
    /// </param>
    public PluginContext(
        string pluginId,
        ILauncher launcher,
        IMatcher matcher,
        IClipboard clipboard,
        IHostLog log,
        IPluginSettings? settings = null,
        IPluginHistory? history = null,
        Func<string>? dataFolder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        Launcher = launcher;
        Matcher = matcher;
        Clipboard = clipboard;
        Log = new PluginLog(pluginId, log);
        Settings = settings ?? PluginSettings.None(pluginId);
        History = history ?? ProviderHistory.Empty;
        _dataFolder = new Lazy<string>(
            dataFolder ?? (() => throw new InvalidOperationException($"{pluginId} was given no data folder.")),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public ILauncher Launcher { get; }

    public IMatcher Matcher { get; }

    public IClipboard Clipboard { get; }

    public IPluginLog Log { get; }

    public IPluginSettings Settings { get; }

    public IPluginHistory History { get; }

    public string DataFolder => _dataFolder.Value;
}

/// <summary>
/// A plugin's view of the host log: every line carries the plugin's id, and an exception is
/// recorded as <see cref="Redaction.Describe"/> has it, never with its message (Hail.md §9).
/// </summary>
public sealed class PluginLog(string pluginId, IHostLog log) : IPluginLog
{
    public void LogInfo(string message) => log.LogInfo($"[{pluginId}] {message}");

    public void LogError(string message, Exception? exception = null) =>
        log.LogError(exception is null ? $"[{pluginId}] {message}" : $"[{pluginId}] {message}: {Redaction.Describe(exception)}");
}

/// <summary>A provider's own part of history (<see cref="IPluginHistory"/>).</summary>
public sealed class ProviderHistory(UsageHistory history, string providerId, TimeProvider? time = null) : IPluginHistory
{
    /// <summary>More than any provider shows for an empty keyword.</summary>
    public const int MaxCount = 64;

    /// <summary>For a provider with no history to read.</summary>
    public static IPluginHistory Empty { get; } = new NoHistory();

    public IReadOnlyList<string> MostPicked(int count) =>
        count <= 0
            ? []
            : [.. history.Top(Math.Min(count, MaxCount), (time ?? TimeProvider.System).GetUtcNow(), providerId).Select(k => k.ResultId)];

    private sealed class NoHistory : IPluginHistory
    {
        public IReadOnlyList<string> MostPicked(int count) => [];
    }
}

/// <summary>What an action is told when it runs.</summary>
public sealed record ActionContext(Query Query) : IActionContext;
