using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Hail.Core.Ports;
using Hail.Core.Updates;

namespace Hail.Windows.Updates;

/// <summary>
/// Where a downloaded installer is kept, and how it is started (Sling's <c>UpdateInstaller</c>).
/// With <see cref="Launching.ShellLauncher"/>, the only code in Hail that starts a process, and it
/// starts exactly one thing: a file Hail itself downloaded into a folder it made, whose checksum
/// it has just verified again.
/// </summary>
/// <remarks>
/// <para>
/// Each download gets a folder of its own under <c>%TEMP%</c>, named with a fresh GUID and
/// created here, so nothing else can have put a file where the installer is about to be written.
/// Old folders are swept at the next check.
/// </para>
/// <para>
/// The installer is started as a plain executable, with no shell and no arguments, and shown as
/// it always is: the user chose a visible install, so it asks its usual questions and they can
/// still cancel it. It runs per user and needs no elevation.
/// </para>
/// </remarks>
public sealed class InstallerFiles(string? temporaryFolder = null)
{
    private const string FolderPrefix = "Hail-Update-";

    /// <summary>A day, so a download whose installer is still running is never pulled out from under it.</summary>
    private static readonly TimeSpan SweepAge = TimeSpan.FromDays(1);

    private readonly string _temporary = temporaryFolder ?? Path.GetTempPath();

    /// <summary>Creates a new, empty folder for one download.</summary>
    public string CreateDownloadFolder()
    {
        var path = Path.Combine(_temporary, FolderPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Deletes download folders left by earlier updates. Never throws.</summary>
    public void SweepOldDownloads(DateTimeOffset now)
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(_temporary, FolderPrefix + "*"))
            {
                try
                {
                    if (now - Directory.GetCreationTimeUtc(folder) >= SweepAge)
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use, or not ours to delete. The next sweep tries again.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The temporary folder itself could not be listed; the next check tries again.
        }
    }

    /// <summary>Deletes one download folder. Never throws.</summary>
    public static void Discard(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Swept at the next check.
        }
    }

    /// <summary>Starts the installer, if it is still the file that was verified.</summary>
    /// <remarks>
    /// The download was hashed as it arrived and then sat closed on disk, so it is hashed again
    /// here through a handle that refuses writers and deletion, and that handle stays open until
    /// Windows has started the process: nothing can replace the file between the check and the start.
    /// </remarks>
    /// <exception cref="UpdateCheckException">The file changed, or Windows would not start it.</exception>
    public static void Launch(DownloadedInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(installer);

        try
        {
            using var pinned = new FileStream(installer.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(pinned), installer.Sha256.Span))
            {
                throw new UpdateCheckException("The installer changed on disk after it was checked, so it was not run.");
            }

            using var process = Process.Start(new ProcessStartInfo(installer.Path)
            {
                UseShellExecute = false,
                WorkingDirectory = installer.Folder,
            }) ?? throw new UpdateCheckException("Windows did not start the installer.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Type and code only: an exception's message can name the path, and the path names the user.
            throw new UpdateCheckException($"The installer could not be started ({ex.GetType().Name}).", ex);
        }
    }
}
