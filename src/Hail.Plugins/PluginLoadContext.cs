using System.Reflection;
using System.Runtime.Loader;
using Hail.Sdk;

namespace Hail.Plugins;

/// <summary>
/// One plugin's assemblies, in a collectible context of their own (Hail.md §6.3), so two
/// plugins can carry different versions of one library without meeting, and a plugin can be
/// unloaded and loaded again without restarting Hail.
/// </summary>
/// <remarks>
/// <para>
/// <c>Hail.Sdk</c> is always the host's own copy, whatever the plugin's folder holds, so the
/// contract's types are the same types on both sides. Everything the framework provides is
/// shared from the host; everything else comes from the plugin's folder, where its
/// <c>.deps.json</c> says.
/// </para>
/// <para>
/// Every managed assembly is read into memory, checked against the hash the user's answer was
/// pinned to, and loaded from those bytes: what runs is exactly what was approved, and the
/// files on disk are not locked, so a plugin author can rebuild while Hail runs and reload. A
/// native library cannot be loaded from memory: it is checked through a handle that forbids
/// writing and deleting it, and loaded from its path while that handle is open; it stays loaded
/// until Hail quits (Windows does not unload it with the context). What Windows then loads for
/// it by itself (its own imports) is not checked here; <see cref="PluginProvider"/> re-reads the
/// whole folder's fingerprint just before the first load, which leaves only that moment.
/// </para>
/// </remarks>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly Assembly Sdk = typeof(IProvider).Assembly;

    private readonly string _folder;
    private readonly PluginFiles _files;
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginId, string folder, string entryPath, PluginFiles files)
        : base($"Hail plugin {pluginId}", isCollectible: true)
    {
        _folder = folder;
        _files = files;
        _resolver = new AssemblyDependencyResolver(entryPath);
    }

    /// <summary>The name <c>Hail.Sdk</c> is known by, which the host always answers itself.</summary>
    public static string SdkName => Sdk.GetName().Name!;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, SdkName, StringComparison.OrdinalIgnoreCase))
        {
            return Sdk;
        }

        // Null: the framework's, shared from the host.
        return _resolver.ResolveAssemblyToPath(assemblyName) is { } path ? LoadVerified(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        if (_resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is not { } path)
        {
            return 0;
        }

        // Held open with no write or delete sharing, so the checked bytes are the loaded ones.
        using var held = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        Verify(path, SHA256Of(held));
        return LoadUnmanagedDllFromPath(path);
    }

    private Assembly LoadVerified(string path)
    {
        var assembly = ReadVerified(path);
        var symbols = Path.ChangeExtension(path, ".pdb");
        if (_files.Hashes.ContainsKey(PluginFiles.Relative(_folder, symbols)) && File.Exists(symbols))
        {
            // With its symbols, so a plugin's failures in the log name its own lines.
            using var pdb = new MemoryStream(ReadVerified(symbols));
            using var dll = new MemoryStream(assembly);
            return LoadFromStream(dll, pdb);
        }

        using var stream = new MemoryStream(assembly);
        return LoadFromStream(stream);
    }

    /// <summary>The file's bytes, if they are the bytes that were approved; otherwise a refusal.</summary>
    private byte[] ReadVerified(string path)
    {
        Approved(path);
        var full = Path.GetFullPath(path);
        if (new FileInfo(full).Length > PluginFiles.MaxBytes)
        {
            throw new PluginRefusedException($"{PluginFiles.Relative(_folder, full)} is larger than a plugin's file can be.");
        }

        var bytes = File.ReadAllBytes(full);
        Verify(path, PluginFiles.HashOf(bytes));
        return bytes;
    }

    /// <summary>The approved hash of <paramref name="path"/>, which must be inside the folder and approved.</summary>
    private string Approved(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = PluginFiles.Relative(_folder, full);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            throw new PluginRefusedException($"It asks for a file outside its folder ({Path.GetFileName(full)}), which Hail does not load.");
        }

        return _files.Hashes.TryGetValue(relative, out var approved)
            ? approved
            : throw new PluginRefusedException($"{relative} was added to its folder after it was enabled.");
    }

    private void Verify(string path, string hash)
    {
        if (!string.Equals(hash, Approved(path), StringComparison.Ordinal))
        {
            throw new PluginRefusedException($"{PluginFiles.Relative(_folder, Path.GetFullPath(path))} has changed since the plugin was enabled.");
        }
    }

    private static string SHA256Of(Stream stream) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
}
