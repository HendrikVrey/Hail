using Hail.Core.Hosting;
using Hail.Core.Plugins;
using Hail.Core.Ports;
using Hail.Persistence;
using Hail.Sdk;

namespace Hail.Plugins;

/// <summary>A plugin as the host holds it: what was found, its settings, and its provider when it is enabled.</summary>
public sealed class PluginEntry(DiscoveredPlugin plugin, PluginSettings settings, PluginProvider? provider)
{
    public DiscoveredPlugin Plugin { get; } = plugin;

    /// <summary>Its settings, the object its context hands it; saved values replace these in place.</summary>
    public PluginSettings Settings { get; } = settings;

    /// <summary>Null unless it is enabled; loaded on its first query.</summary>
    public PluginProvider? Provider { get; } = provider;

    public string Id => Plugin.Id;

    public string Name => Plugin.Name;

    public PluginStatus Status => Plugin.Status;

    /// <summary>Whether the user has something to answer: a new plugin, or one whose files changed.</summary>
    public bool AwaitsAnswer => Status is PluginStatus.New or PluginStatus.Changed;
}

/// <summary>What one look at the plugins folder found, and the providers it would put in force.</summary>
public sealed record PluginScan(IReadOnlyList<PluginEntry> Entries, IReadOnlyList<ProviderRegistration> Registrations);

/// <summary>What unloading the previous plugins found (Hail.md §6.3): the ids that stayed in memory.</summary>
public sealed record UnloadReport(IReadOnlyList<string> Unloaded, IReadOnlyList<string> Lingering);

/// <summary>
/// The plugins folder, the user's answers and the plugins' settings, in one place (Hail.md
/// §6.3, §9): it finds plugins, records what the user answers about each, and makes providers
/// for the enabled ones. It shows nothing; Hail.App asks the questions and draws the forms.
/// </summary>
/// <remarks>
/// A change of any kind (a new answer, Reload plugins) is a new <see cref="Scan"/>: every
/// plugin is looked at again and every enabled one gets a fresh provider, loaded on its first
/// use. Simple beats clever here; a scan hashes a few files and loads nothing.
/// </remarks>
public sealed class PluginManager
{
    private readonly HailPaths _paths;
    private readonly PluginApprovalStore _approvals;
    private readonly PluginSettingsStore _settings;
    private readonly ISecretProtector _protector;
    private readonly IHostLog _log;
    private readonly Lock _gate = new();
    private IReadOnlyList<PluginEntry> _entries = [];

    public PluginManager(HailPaths paths, PluginApprovalStore approvals, PluginSettingsStore settings, ISecretProtector protector, IHostLog log, SdkVersion? host = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(log);
        _paths = paths;
        _approvals = approvals;
        _settings = settings;
        _protector = protector;
        _log = log;
        Host = host ?? SdkVersion.Of(typeof(IProvider).Assembly.GetName().Version!);
    }

    /// <summary>How long an unloaded context is given to go before the report calls it still in memory.</summary>
    internal TimeSpan UnloadWait { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>The SDK version this host speaks: the <c>Hail.Sdk</c> it carries.</summary>
    public SdkVersion Host { get; }

    public string PluginsFolder => _paths.Plugins;

    /// <summary>Whether the user's answers can be written now (the log says why not when they cannot).</summary>
    public bool CanRecordAnswers => _approvals.CanRecord;

    /// <summary>The plugins in force: the last scan adopted.</summary>
    public IReadOnlyList<PluginEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries;
            }
        }
    }

    /// <summary>
    /// Looks at the plugins folder and makes a provider for every enabled plugin, without
    /// putting any of it in force. Reads and hashes files, so call it off the UI thread.
    /// </summary>
    /// <param name="contextFor">The context each enabled plugin's provider is given.</param>
    public PluginScan Scan(Func<PluginEntry, IPluginContext> contextFor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contextFor);

        var entries = new List<PluginEntry>();
        var registrations = new List<ProviderRegistration>();
        foreach (var plugin in PluginDiscovery.Discover(_paths.Plugins, _approvals.For, Host, ct))
        {
            var schema = plugin.Manifest?.Settings ?? [];
            var settings = new PluginSettings(plugin.Id, schema, _settings.ValuesFor(plugin.Id, schema), _protector, _log);
            var provider = plugin.Status == PluginStatus.Enabled ? new PluginProvider(plugin, Host, _log) : null;
            var entry = new PluginEntry(plugin, settings, provider);
            entries.Add(entry);

            if (plugin.Status == PluginStatus.Invalid)
            {
                _log.LogError($"Plugin folder {Path.GetFileName(plugin.Folder)} is not used: {plugin.Problem}");
            }

            if (provider is not null)
            {
                var manifest = plugin.Manifest!;
                registrations.Add(new ProviderRegistration(manifest.Id, manifest.Name, provider, contextFor(entry))
                {
                    Keywords = [.. manifest.Keywords.Select(k => new ProviderKeyword(k, manifest.Name))],
                    IsGlobal = manifest.IsGlobal,
                    Debounce = manifest.Debounce,
                });
            }
        }

        return new PluginScan(entries, registrations);
    }

    /// <summary>Puts <paramref name="scan"/>'s plugins in force; returns the ones it replaces, to unload.</summary>
    public IReadOnlyList<PluginEntry> Adopt(PluginScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        lock (_gate)
        {
            var previous = _entries;
            _entries = scan.Entries;
            return previous;
        }
    }

    /// <summary>
    /// Records the user's answer for <paramref name="entry"/>'s files as they are now. Throws
    /// <see cref="IOException"/> when it could not be written.
    /// </summary>
    public void Answer(PluginEntry entry, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Plugin.Files is not { } files || entry.Status == PluginStatus.Invalid)
        {
            throw new InvalidOperationException("A plugin that cannot be used cannot be enabled.");
        }

        _approvals.Record(entry.Id, new PluginApproval(enabled, files.Fingerprint));
        _log.LogInfo($"Plugin {entry.Id} {(enabled ? "enabled" : "kept off")} by the user.");
    }

    /// <summary>
    /// Saves <paramref name="values"/> as <paramref name="entry"/>'s settings, and hands them to
    /// the plugin at once: to the entry in force under the same id too, since a reload may have
    /// replaced the one the form was opened on. Throws <see cref="IOException"/> when they could
    /// not be written.
    /// </summary>
    public void SaveSettings(PluginEntry entry, IReadOnlyDictionary<string, SettingValue> values)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(values);
        _settings.Save(entry.Id, values);
        entry.Settings.Replace(values);
        if (Entries.FirstOrDefault(e => string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase)) is { } current && !ReferenceEquals(current, entry))
        {
            current.Settings.Replace(values);
        }

        _log.LogInfo($"Settings of plugin {entry.Id} saved.");
    }

    /// <summary>
    /// Disposes and unloads <paramref name="entries"/>' providers, then, unless
    /// <paramref name="verify"/> is false (Hail is quitting), asks the garbage collector whether
    /// each context really went. The host must have dropped every result of theirs first
    /// (their actions are leased, so a result still held keeps nothing), or they stay, and the
    /// report says so.
    /// </summary>
    /// <param name="releaseCaches">
    /// Clears what the UI framework itself caches of loaded assemblies, run before every
    /// collection: the host's UI may refer to a plugin's assembly in ways no plugin and no
    /// host code can see (Hail.App's <c>WpfAssemblyCaches</c>).
    /// </param>
    public async Task<UnloadReport> UnloadAsync(IReadOnlyList<PluginEntry> entries, TimeSpan disposeBudget, bool verify = true, Action? releaseCaches = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // All at once, so each plugin has the whole budget however many there are.
        var providers = entries.Select(e => e.Provider).OfType<PluginProvider>().ToArray();
        var unloaded = await Task.WhenAll(providers.Select(p => p.UnloadAsync(disposeBudget))).ConfigureAwait(false);
        var contexts = providers
            .Zip(unloaded, (provider, weak) => (provider.Plugin.Id, Context: weak))
            .Where(c => c.Context is not null)
            .Select(c => (c.Id, Context: c.Context!))
            .ToList();

        if (contexts.Count == 0 || !verify)
        {
            return new UnloadReport([.. contexts.Select(c => c.Id)], []);
        }

        // Not at once: finalizers and the runtime's own bookkeeping take a few collections, and
        // a plugin's thread may still be finishing the Dispose it was just given.
        var deadline = DateTime.UtcNow + UnloadWait;
        while (contexts.Any(c => c.Context.IsAlive) && DateTime.UtcNow < deadline)
        {
            releaseCaches = Release(releaseCaches);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (contexts.Any(c => c.Context.IsAlive))
            {
                await Task.Delay(200).ConfigureAwait(false);
            }
        }

        var report = new UnloadReport(
            [.. contexts.Where(c => !c.Context.IsAlive).Select(c => c.Id)],
            [.. contexts.Where(c => c.Context.IsAlive).Select(c => c.Id)]);

        foreach (var id in report.Unloaded)
        {
            _log.LogInfo($"Plugin {id} unloaded.");
        }

        foreach (var id in report.Lingering)
        {
            _log.LogError($"Plugin {id} was unloaded but is still in memory: something still refers to it (a thread it left running, an event it did not unhook). It is freed when Hail quits.");
        }

        return report;
    }

    /// <summary>Runs the host's cache release; one that fails is logged and not tried again this unload.</summary>
    private Action? Release(Action? releaseCaches)
    {
        try
        {
            releaseCaches?.Invoke();
            return releaseCaches;
        }
#pragma warning disable CA1031 // It reaches into a UI framework's internals; a failure there must not stop the unload.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.LogError($"Clearing the UI's caches of plugin assemblies failed: {Redaction.Describe(ex)}");
            return null;
        }
    }
}
