using Hail.Core.Ports;
using Hail.Core.Updates;
using Hail.Windows.Updates;

namespace Hail.Updates;

/// <summary>
/// The updater's outside world in one place: GitHub through <see cref="UpdateClient"/>, the
/// download folder and the installer through <see cref="InstallerFiles"/>.
/// </summary>
public sealed class UpdateService : IUpdateService, IDisposable
{
    private readonly UpdateClient _client;
    private readonly InstallerFiles _files;
    private readonly TimeProvider _time;

    public UpdateService(ReleaseVersion running, TimeProvider? time = null)
        : this(new UpdateClient(running), new InstallerFiles(), time)
    {
    }

    internal UpdateService(UpdateClient client, InstallerFiles files, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(files);
        _client = client;
        _files = files;
        _time = time ?? TimeProvider.System;
    }

    public Task<LatestRelease> GetLatestAsync(CancellationToken ct) => _client.GetLatestAsync(ct);

    public async Task<DownloadedInstaller> DownloadAsync(LatestRelease release, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);

        string folder;
        try
        {
            folder = _files.CreateDownloadFolder();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateCheckException($"There is nowhere to put the download ({ex.GetType().Name}).", ex);
        }

        try
        {
            var path = await _client.DownloadAsync(release, folder, progress, ct).ConfigureAwait(false);
            return new DownloadedInstaller(path, folder, release.Sha256);
        }
        catch
        {
            InstallerFiles.Discard(folder);
            throw;
        }
    }

    public void Launch(DownloadedInstaller installer) => InstallerFiles.Launch(installer);

    public void Discard(DownloadedInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(installer);
        InstallerFiles.Discard(installer.Folder);
    }

    public void SweepOldDownloads() => _files.SweepOldDownloads(_time.GetUtcNow());

    public void Dispose() => _client.Dispose();
}
