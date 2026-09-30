using System.Reflection;
using System.Runtime.CompilerServices;
using Hail.Core.Hosting;
using Hail.Core.Plugins;
using Hail.Core.Ports;
using Hail.Sdk;

namespace Hail.Plugins;

/// <summary>
/// Stands in for an enabled plugin's provider until the first query reaches it, then loads the
/// plugin into its own context, makes its provider and passes everything on (Hail.md §6.3):
/// nothing of a plugin is loaded before it is used. The supervisor drives this exactly as it
/// drives a built-in provider, faults and budgets included.
/// </summary>
/// <remarks>
/// Loaded once. After <see cref="UnloadAsync"/> it answers nothing, and the host makes a new
/// one for the next load (Reload plugins).
/// </remarks>
public sealed class PluginProvider : IProvider, IRecall, IProviderProxy
{
    /// <summary>How long a plugin that failed to start, or was unloaded while starting, is given to dispose.</summary>
    private static readonly TimeSpan InitialDisposeBudget = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly ActionLease _lease = new();
    private readonly SdkVersion _host;
    private readonly IHostLog _log;
    private PluginLoadContext? _context;
    private IProvider? _inner;
    private bool _unloaded;

    public PluginProvider(DiscoveredPlugin plugin, SdkVersion host, IHostLog log)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(log);
        if (plugin is not { Status: PluginStatus.Enabled, Manifest: not null, Files: not null })
        {
            throw new ArgumentException("Only an enabled plugin is ever loaded.", nameof(plugin));
        }

        Plugin = plugin;
        _host = host;
        _log = log;
    }

    public DiscoveredPlugin Plugin { get; }

    /// <summary>Why loading it failed, in the host's words, for the tray; null while it has not.</summary>
    public string? LoadProblem { get; private set; }

    public bool IsLoaded => Volatile.Read(ref _inner) is not null;

    public bool Recalls => Volatile.Read(ref _inner) is IRecall;

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var box = new StrongBox<IProvider?>(Load());
        try
        {
            await box.Value!.InitializeAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The supervisor switches it off; nothing of it is kept loaded meanwhile.
            LoadProblem = $"it failed to start ({ex.GetType().Name}).";
            await DisposeProviderAsync(box, InitialDisposeBudget).ConfigureAwait(false);
            _ = UnloadContext();
            throw;
        }

        lock (_gate)
        {
            if (!_unloaded)
            {
                _inner = box.Value;
                box.Value = null;
                _log.LogInfo($"Plugin {Plugin.Id} {Plugin.Manifest!.Version} loaded.");
                return;
            }
        }

        // Reload plugins arrived while it was starting: it is not wanted any more.
        await DisposeProviderAsync(box, InitialDisposeBudget).ConfigureAwait(false);
        _ = UnloadContext();
    }

    /// <summary>The plugin's results, each with its actions leased (<see cref="ActionLease"/>).</summary>
    public async IAsyncEnumerable<Result> QueryAsync(Query query, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var result in Inner().QueryAsync(query, ct).ConfigureAwait(false))
        {
            yield return _lease.Adopt(result);
        }
    }

    public async ValueTask<Recollection> RecallAsync(string id, CancellationToken ct)
    {
        if (Inner() is not IRecall recall)
        {
            return Recollection.Unknown;
        }

        var answer = await recall.RecallAsync(id, ct).ConfigureAwait(false);
        return answer is Recollection.Found { Result: { } result } ? Recollection.Of(_lease.Adopt(result)) : answer;
    }

    /// <summary>
    /// Disposes the plugin's provider (waiting at most <paramref name="disposeBudget"/>) and
    /// unloads its context. The weak reference says, once the garbage collector has run,
    /// whether the context really went (Hail.md §6.3); null when nothing had been loaded.
    /// </summary>
    public async Task<WeakReference?> UnloadAsync(TimeSpan disposeBudget)
    {
        IProvider? inner;
        lock (_gate)
        {
            _unloaded = true;
            inner = _inner;
            _inner = null;
        }

        // Whatever the box or its window still holds of the plugin's results lets go of its code now.
        _lease.Revoke();

        if (inner is not null)
        {
            // Handed over in a box and the local cleared, so nothing on this method's own
            // state machine is still holding the plugin when the context is asked to go.
            var box = new StrongBox<IProvider?>(inner);
            inner = null;
            await DisposeProviderAsync(box, disposeBudget).ConfigureAwait(false);
        }

        return UnloadContext();
    }

    private IProvider Inner() =>
        Volatile.Read(ref _inner) ?? throw new InvalidOperationException($"Plugin {Plugin.Id} was queried before it loaded.");

    private IProvider Load()
    {
        lock (_gate)
        {
            if (_unloaded)
            {
                throw new InvalidOperationException($"Plugin {Plugin.Id} has been unloaded.");
            }

            if (_context is not null)
            {
                throw new InvalidOperationException($"Plugin {Plugin.Id} is loaded once.");
            }

            try
            {
                // The folder as it is now, not as it was at the scan: loading is lazy, and a file
                // added since (a DLL Windows would load for a native library, say) is a change.
                if (!string.Equals(PluginFiles.Read(Plugin.Folder).Fingerprint, Plugin.Files!.Fingerprint, StringComparison.Ordinal))
                {
                    throw new PluginRefusedException("Its files have changed since it was enabled.");
                }

                _context = new PluginLoadContext(Plugin.Id, Plugin.Folder, Plugin.EntryPath, Plugin.Files!);
                return Make(_context);
            }
            catch (Exception ex)
            {
                // A refusal inside the load context's own resolver reaches here wrapped by the runtime.
                LoadProblem = RefusalIn(ex)?.Message ?? Describe(ex);
                _log.LogError($"Plugin {Plugin.Id} could not be loaded: {LoadProblem} {Redaction.Describe(ex)}");
                _context?.Unload();
                _context = null;
                throw;
            }
        }
    }

    /// <summary>Loads the entry assembly and makes its provider, refusing with a sentence at each step that can fail.</summary>
    private IProvider Make(PluginLoadContext context)
    {
        var manifest = Plugin.Manifest!;
        var assembly = context.LoadFromAssemblyName(new AssemblyName(Path.GetFileNameWithoutExtension(manifest.Entry)));

        var sdk = assembly.GetReferencedAssemblies().FirstOrDefault(a => string.Equals(a.Name, PluginLoadContext.SdkName, StringComparison.OrdinalIgnoreCase));
        if (sdk?.Version is null)
        {
            throw new PluginRefusedException($"{manifest.Entry} does not use Hail.Sdk, so it cannot be a Hail plugin.");
        }

        if (SdkCompatibility.Problem(SdkVersion.Of(sdk.Version), _host) is { } problem)
        {
            throw new PluginRefusedException(problem);
        }

        var type = assembly.GetType(manifest.TypeName, throwOnError: false)
            ?? throw new PluginRefusedException($"{manifest.Entry} has no type {manifest.TypeName}.");

        if (!typeof(IProvider).IsAssignableFrom(type) || !type.IsClass || type.IsAbstract)
        {
            throw new PluginRefusedException($"{manifest.TypeName} is not a provider (a class that implements IProvider).");
        }

        if (type.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new PluginRefusedException($"{manifest.TypeName} needs a public constructor that takes no arguments.");
        }

        try
        {
            return (IProvider)Activator.CreateInstance(type)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new PluginRefusedException($"{manifest.TypeName}'s constructor failed.", ex.InnerException);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference? UnloadContext()
    {
        PluginLoadContext? context;
        lock (_gate)
        {
            context = _context;
            _context = null;
        }

        if (context is null)
        {
            return null;
        }

        context.Unload();
        return new WeakReference(context, trackResurrection: true);
    }

    /// <summary>
    /// Disposes the provider on the pool, where a plugin that blocks costs a pool thread and not
    /// the host; the box is emptied whatever happens, so it holds nothing afterwards.
    /// </summary>
    private async Task DisposeProviderAsync(StrongBox<IProvider?> box, TimeSpan budget)
    {
        var disposing = Task.Run(() => DisposeHeld(box), CancellationToken.None);
        try
        {
            await disposing.WaitAsync(budget).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.LogError($"Plugin {Plugin.Id} did not finish disposing within {budget.TotalSeconds:0} s; it may stay in memory until Hail quits.");
        }
#pragma warning disable CA1031 // The plugin's own failure while disposing; logged, and the unload goes on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.LogError($"Plugin {Plugin.Id} failed while disposing: {Redaction.Describe(ex)}");
        }
    }

    private static void DisposeHeld(StrongBox<IProvider?> box)
    {
        try
        {
            switch (box.Value)
            {
                case IAsyncDisposable asynchronous:
                    asynchronous.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        finally
        {
            box.Value = null;
        }
    }

    private static PluginRefusedException? RefusalIn(Exception? ex)
    {
        for (var depth = 0; ex is not null && depth < 8; depth++, ex = ex.InnerException)
        {
            if (ex is PluginRefusedException refused)
            {
                return refused;
            }
        }

        return null;
    }

    /// <summary>A failure that is not the host's own refusal: its type only, as the log has it.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        FileLoadException or FileNotFoundException or BadImageFormatException => $"one of its assemblies could not be loaded ({ex.GetType().Name}).",
        _ => $"loading it failed ({ex.GetType().Name}).",
    };
}
