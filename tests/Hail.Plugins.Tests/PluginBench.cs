using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Hail.Core.Hosting;
using Hail.Core.Matching;
using Hail.Core.Ports;
using Hail.Persistence;
using Hail.Sdk;

namespace Hail.Plugins.Tests;

/// <summary>
/// A temporary Hail folder with a plugins folder in it, into which tests install plugins built
/// with the solution, and the real manager over it.
/// </summary>
internal sealed class PluginBench : IDisposable
{
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    public static readonly string RepoRoot = FindRepoRoot();

    public PluginBench()
    {
        Root = Path.Combine(Path.GetTempPath(), "hail-plugin-tests", Guid.NewGuid().ToString("N"));
        Paths = new HailPaths(Root);
        Directory.CreateDirectory(Paths.Plugins);
    }

    public string Root { get; }

    public HailPaths Paths { get; }

    public RecordingLog Log { get; } = new();

    public FakeProtector Protector { get; } = new();

    public static string BuiltTestPlugin(string project) =>
        Path.Combine(RepoRoot, "tests", "plugins", project, "bin", Configuration, "net10.0");

    public static string BuiltEverything() =>
        Path.Combine(RepoRoot, "plugins", "Hail.Plugin.Everything", "bin", Configuration, "net10.0-windows");

    /// <summary>
    /// Copies <paramref name="built"/> into the plugins folder as <paramref name="id"/>, with a
    /// manifest naming <paramref name="type"/> (or the built folder's own plugin.json when
    /// <paramref name="type"/> is null), edited by <paramref name="edit"/>.
    /// </summary>
    public string Install(string id, string built, string? type, string entry, Action<JsonObject>? edit = null, string? folderName = null)
    {
        var folder = Path.Combine(Paths.Plugins, folderName ?? id);
        CopyFolder(built, folder);

        var manifest = type is null
            ? (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "plugin.json")), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!
            : new JsonObject
            {
                ["id"] = id,
                ["name"] = id,
                ["publisher"] = "Tests",
                ["version"] = "1.0.0",
                ["sdk"] = "1.0",
                ["entry"] = entry,
                ["type"] = type,
                ["keywords"] = new JsonArray("t"),
            };

        edit?.Invoke(manifest);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), manifest.ToJsonString());
        return folder;
    }

    public string InstallAlpha(string id = "tests.alpha", string type = "Hail.TestPlugin.Alpha.AlphaProvider", Action<JsonObject>? edit = null) =>
        Install(id, BuiltTestPlugin("Hail.TestPlugin.Alpha"), type, "Hail.TestPlugin.Alpha.dll", edit);

    public string InstallBeta(string id = "tests.beta") =>
        Install(id, BuiltTestPlugin("Hail.TestPlugin.Beta"), "Hail.TestPlugin.Beta.BetaProvider", "Hail.TestPlugin.Beta.dll");

    /// <summary>A manager over this folder, reading the answers and settings on disk now.</summary>
    public PluginManager Manager()
    {
        var approvals = new PluginApprovalStore(Paths);
        Assert.Null(approvals.Load());
        var settings = new PluginSettingsStore(Paths);
        Assert.Null(settings.Load());
        return new PluginManager(Paths, approvals, settings, Protector, Log) { UnloadWait = TimeSpan.FromSeconds(3) };
    }

    public PluginScan Scan(PluginManager manager) =>
        manager.Scan(entry => new PluginContext(entry.Id, new NullLauncher(), new FuzzyMatcher(), new NullClipboard(), Log, entry.Settings));

    /// <summary>Enables every plugin the folder holds now, as the user would, one answer each.</summary>
    public void EnableAll()
    {
        var manager = Manager();
        foreach (var entry in Scan(manager).Entries.Where(e => e.AwaitsAnswer))
        {
            manager.Answer(entry, enabled: true);
        }
    }

    private static readonly ConditionalWeakTable<IReadOnlyList<ProviderRegistration>, Supervisor> Supervisors = [];

    /// <summary>
    /// The titles the providers answer <paramref name="text"/> with, through the real
    /// supervisor: one per set of registrations, as the host keeps one, so each provider is
    /// initialised once however many queries a test sends.
    /// </summary>
    public static async Task<string[]> QueryAsync(IReadOnlyList<ProviderRegistration> registrations, string text)
    {
        var supervisor = Supervisors.GetValue(registrations, r => new Supervisor(r, new RecordingLog(), new SupervisorOptions { FirstFrameBudget = TimeSpan.FromSeconds(5) }));
        var parsed = new QueryParser(supervisor.Providers).Parse(text);
        QueryUpdate? last = null;
        await foreach (var update in supervisor.RunAsync(parsed, CancellationToken.None))
        {
            last = update;
        }

        return [.. last!.Results.Select(r => r.Result.Title).Order(StringComparer.Ordinal)];
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A native library a test loaded stays mapped until the test process ends.
        }
    }

    private static void CopyFolder(string from, string to)
    {
        Assert.True(Directory.Exists(from), $"The test plugin was not built: {from}");
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hail.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find Hail.slnx.");
    }
}

internal sealed class RecordingLog : IHostLog
{
    private readonly List<string> _lines = [];

    public string[] Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public void LogInfo(string message)
    {
        lock (_lines)
        {
            _lines.Add("INFO " + message);
        }
    }

    public void LogError(string message, Exception? exception = null)
    {
        lock (_lines)
        {
            _lines.Add("ERROR " + message + (exception is null ? string.Empty : " " + exception));
        }
    }
}

/// <summary>Stands in for DPAPI: reversible, and bound to its purpose as DPAPI's entropy is.</summary>
internal sealed class FakeProtector : ISecretProtector
{
    public byte[] Protect(byte[] plain, byte[] purpose) => [.. SHA256.HashData(purpose), .. plain];

    public byte[] Unprotect(byte[] protectedData, byte[] purpose) =>
        protectedData.AsSpan(0, 32).SequenceEqual(SHA256.HashData(purpose))
            ? protectedData[32..]
            : throw new CryptographicException("Another purpose.");
}

internal sealed class NullLauncher : ILauncher
{
    public ValueTask LaunchAppAsync(string appId, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask LaunchAppAsAdministratorAsync(string appId, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask OpenUriAsync(Uri uri, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask OpenPathAsync(string path, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask ShowInFolderAsync(string path, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class NullClipboard : IClipboard
{
    public ValueTask SetTextAsync(string text, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask SetFileAsync(string path, CancellationToken ct) => throw new NotSupportedException();
}
