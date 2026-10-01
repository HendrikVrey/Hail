using System.Security.Cryptography;
using Hail.Core.Ports;
using Hail.Core.Updates;
using Hail.Windows.Updates;

namespace Hail.Updates.Tests;

/// <summary>The download folder, and the refusal to start an installer that changed after it was checked.</summary>
public sealed class InstallerFilesTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "hail-installer-tests", Guid.NewGuid().ToString("N"));

    public InstallerFilesTests() => Directory.CreateDirectory(_temporary);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temporary, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Each_download_gets_a_new_empty_folder()
    {
        var files = new InstallerFiles(_temporary);

        var first = files.CreateDownloadFolder();
        var second = files.CreateDownloadFolder();

        Assert.NotEqual(first, second);
        Assert.Empty(Directory.EnumerateFileSystemEntries(first));
        Assert.StartsWith(Path.Combine(_temporary, "Hail-Update-"), first, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_old_download_folders_are_swept_and_nothing_else()
    {
        var files = new InstallerFiles(_temporary);
        var old = files.CreateDownloadFolder();
        var fresh = files.CreateDownloadFolder();
        var unrelated = Directory.CreateDirectory(Path.Combine(_temporary, "Something-Else")).FullName;
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        Directory.SetCreationTimeUtc(unrelated, DateTime.UtcNow.AddDays(-2));

        files.SweepOldDownloads(DateTimeOffset.UtcNow);

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public void An_installer_that_changed_after_it_was_checked_is_not_started()
    {
        var folder = new InstallerFiles(_temporary).CreateDownloadFolder();
        var path = Path.Combine(folder, "Hail-Setup-9.9.9.exe");
        File.WriteAllBytes(path, [1, 2, 3]);
        var checkedHash = SHA256.HashData(new byte[] { 1, 2, 4 });

        var refused = Assert.Throws<UpdateCheckException>(() => InstallerFiles.Launch(new DownloadedInstaller(path, folder, checkedHash)));

        Assert.Contains("changed on disk", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_installer_is_a_sentence_that_does_not_name_the_path()
    {
        var folder = Path.Combine(_temporary, "gone");
        var refused = Assert.Throws<UpdateCheckException>(
            () => InstallerFiles.Launch(new DownloadedInstaller(Path.Combine(folder, "Hail-Setup.exe"), folder, new byte[32])));

        Assert.DoesNotContain(_temporary, refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discarding_a_folder_that_is_not_there_says_nothing() =>
        InstallerFiles.Discard(Path.Combine(_temporary, "never-made"));
}
