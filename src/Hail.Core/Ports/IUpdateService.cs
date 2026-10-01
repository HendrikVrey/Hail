using Hail.Core.Updates;

namespace Hail.Core.Ports;

/// <summary>An installer that has been downloaded and matched the checksum GitHub published.</summary>
/// <param name="Path">The installer.</param>
/// <param name="Folder">The folder made for this download alone, deleted with it.</param>
/// <param name="Sha256">What the installer was verified against, to verify it again just before it runs.</param>
public sealed record DownloadedInstaller(string Path, string Folder, ReadOnlyMemory<byte> Sha256);

/// <summary>
/// Everything the updater needs from outside Core: GitHub, the disk and starting the installer.
/// The host composes it from <c>Hail.Updates</c> (the network) and <c>Hail.Windows</c> (the
/// temporary folder and the process), so the rules in <see cref="UpdateCoordinator"/> are tested
/// without either.
/// </summary>
public interface IUpdateService
{
    /// <summary>Asks GitHub for the newest published release.</summary>
    /// <exception cref="UpdateCheckException">No usable answer, with a sentence saying why.</exception>
    Task<LatestRelease> GetLatestAsync(CancellationToken ct);

    /// <summary>Downloads the release's installer into a folder of its own and proves it is the file GitHub holds.</summary>
    /// <exception cref="UpdateCheckException">It failed or did not match; nothing is left on disk.</exception>
    Task<DownloadedInstaller> DownloadAsync(LatestRelease release, IProgress<double>? progress, CancellationToken ct);

    /// <summary>Starts the installer, if it is still the file that was verified.</summary>
    /// <exception cref="UpdateCheckException">It changed, or Windows would not start it.</exception>
    void Launch(DownloadedInstaller installer);

    /// <summary>Deletes a download that will not be run. Never throws.</summary>
    void Discard(DownloadedInstaller installer);

    /// <summary>Deletes downloads left by earlier updates. Never throws.</summary>
    void SweepOldDownloads();
}

/// <summary>Reads and writes <see cref="UpdateState"/>. Never throws: a lost write costs one extra check.</summary>
public interface IUpdateStateStore
{
    UpdateState Load();

    bool Save(UpdateState state);
}
