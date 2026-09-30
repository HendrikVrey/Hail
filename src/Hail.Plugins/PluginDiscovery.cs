using System.Text;
using Hail.Core.Plugins;

namespace Hail.Plugins;

/// <summary>A folder found in the plugins folder, and what the host makes of it.</summary>
/// <param name="Folder">The plugin's folder, in full.</param>
/// <param name="Manifest">What its <c>plugin.json</c> says; null when it could not be read.</param>
/// <param name="Files">Its files and their fingerprint; null when they could not be read.</param>
/// <param name="Problem">Why it is <see cref="PluginStatus.Invalid"/>, in the host's words; null otherwise.</param>
public sealed record DiscoveredPlugin(string Folder, PluginManifest? Manifest, PluginFiles? Files, PluginStatus Status, string? Problem)
{
    /// <summary>The plugin's id, or its folder's name when the manifest could not say.</summary>
    public string Id => Manifest?.Id ?? Path.GetFileName(Folder);

    /// <summary>What the tray and the consent call it.</summary>
    public string Name => Manifest?.Name ?? Path.GetFileName(Folder);

    public string EntryPath => Path.Combine(Folder, Manifest?.Entry ?? throw new InvalidOperationException("The plugin has no manifest."));
}

/// <summary>
/// Finds plugins (Hail.md §6.3): one folder each under the plugins folder, each with a
/// <c>plugin.json</c>. Reads manifests and hashes files, and loads nothing: whether a plugin is
/// used is decided before any of its code runs.
/// </summary>
public static class PluginDiscovery
{
    /// <summary>More plugins than anyone installs; the rest are not looked at.</summary>
    public const int MaxPlugins = 64;

    public const string ManifestFile = "plugin.json";

    /// <summary>
    /// Every plugin folder under <paramref name="pluginsFolder"/>, by name, with its status. A
    /// folder that does not exist is no plugins.
    /// </summary>
    /// <param name="approvalFor">The user's answer for a plugin id, or null.</param>
    /// <param name="host">The SDK version this host speaks.</param>
    public static IReadOnlyList<DiscoveredPlugin> Discover(
        string pluginsFolder,
        Func<string, PluginApproval?> approvalFor,
        SdkVersion host,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsFolder);
        ArgumentNullException.ThrowIfNull(approvalFor);

        if (!Directory.Exists(pluginsFolder))
        {
            return [];
        }

        return [.. new DirectoryInfo(pluginsFolder)
            .EnumerateDirectories()
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxPlugins)
            .Select(folder => Examine(folder, approvalFor, host, ct))];
    }

    private static DiscoveredPlugin Examine(DirectoryInfo folder, Func<string, PluginApproval?> approvalFor, SdkVersion host, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DiscoveredPlugin Invalid(string problem, PluginManifest? manifest = null) =>
            new(folder.FullName, manifest, null, PluginStatus.Invalid, problem);

        try
        {
            if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Invalid("Its folder is a link; a plugin's folder must hold its own files.");
            }

            var manifestFile = new FileInfo(Path.Combine(folder.FullName, ManifestFile));
            if (!manifestFile.Exists)
            {
                return Invalid($"It has no {ManifestFile}.");
            }

            if (manifestFile.Length > ManifestReader.MaxBytes)
            {
                return Invalid($"Its {ManifestFile} is larger than a manifest can be.");
            }

            // Read once, and checked below against the hash the fingerprint took, so the manifest
            // obeyed is the manifest the user's answer is pinned to.
            var manifestBytes = File.ReadAllBytes(manifestFile.FullName);
            var read = ManifestReader.Read(Text(manifestBytes), folder.Name);
            if (read.Manifest is not { } manifest)
            {
                return Invalid(read.Problem!);
            }

            if (SdkCompatibility.Problem(manifest.Sdk, host) is { } versionProblem)
            {
                return Invalid(versionProblem, manifest);
            }

            if (!File.Exists(Path.Combine(folder.FullName, manifest.Entry)))
            {
                return Invalid($"Its assembly {manifest.Entry} is not in its folder.", manifest);
            }

            var files = PluginFiles.Read(folder.FullName, ct);
            if (!files.Hashes.TryGetValue(ManifestFile, out var manifestHash)
                || !string.Equals(manifestHash, PluginFiles.HashOf(manifestBytes), StringComparison.Ordinal))
            {
                return Invalid($"Its {ManifestFile} changed while it was being read; reload plugins to look again.", manifest);
            }

            var status = PluginApproval.StatusOf(approvalFor(manifest.Id), files.Fingerprint);
            return new DiscoveredPlugin(folder.FullName, manifest, files, status, null);
        }
        catch (PluginRefusedException refused)
        {
            return Invalid(refused.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Invalid($"Its files could not be read ({ex.GetType().Name}).");
        }
    }

    /// <summary>UTF-8, with a byte order mark (which some editors write) left out.</summary>
    private static string Text(byte[] bytes)
    {
        ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
        var span = bytes.AsSpan();
        return Encoding.UTF8.GetString(span.StartsWith(bom) ? span[bom.Length..] : span);
    }
}
