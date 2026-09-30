using System.Diagnostics;
using System.Runtime.InteropServices;
using Hail.Core.Ports;
using Hail.Windows.Interop;

namespace Hail.Windows.Apps;

/// <summary>What one read of the AppsFolder found.</summary>
/// <param name="Count">Entries listed.</param>
/// <param name="Packaged">How many of them are packaged (MSIX or Store) apps.</param>
/// <param name="Elapsed">How long the read took.</param>
public sealed record CatalogRefresh(int Count, int Packaged, TimeSpan Elapsed);

/// <summary>
/// The Start menu's apps, read from the shell's AppsFolder (Hail.md §7.1): desktop shortcuts
/// from both Start Menu folders and packaged apps in one enumeration, each with the id the
/// shell itself launches it by.
/// </summary>
/// <remarks>
/// <para>
/// The list is replaced whole on each refresh and read without a lock: a reader sees the old
/// list or the new one, never a list being built.
/// </para>
/// <para>
/// Read at startup and again when the box is summoned, if the last read is older than
/// <see cref="StaleAfter"/>, off the UI thread and with no timer running while the box is
/// hidden. Change notifications on the Start Menu folders and the package catalogue (Hail.md
/// §7.1) would replace that; they wait until a stale list is seen to matter.
/// </para>
/// </remarks>
public sealed class ShellAppCatalog(StaWorker worker) : IAppCatalog
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    private Snapshot _snapshot = Snapshot.Empty;
    private long _lastRefreshTicks;
    private int _refreshing;

    public IReadOnlyList<AppEntry> Apps => _snapshot.Apps;

    /// <summary>Whether <paramref name="id"/> is an app the last read listed.</summary>
    public bool Contains(string id) => _snapshot.Ids.Contains(id);

    public bool IsStale => Volatile.Read(ref _lastRefreshTicks) is var ticks && (ticks == 0 || Stopwatch.GetElapsedTime(ticks) > StaleAfter);

    /// <summary>
    /// Reads the AppsFolder again, off the calling thread. Returns null without reading when a
    /// refresh is already running, so a burst of summons costs one read.
    /// </summary>
    public async Task<CatalogRefresh?> RefreshAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return null;
        }

        try
        {
            var started = Stopwatch.GetTimestamp();
            var apps = await worker.RunAsync(ReadAppsFolder, ct).ConfigureAwait(false);
            _snapshot = new Snapshot(apps);
            Volatile.Write(ref _lastRefreshTicks, Stopwatch.GetTimestamp());

            return new CatalogRefresh(apps.Count, apps.Count(a => IsPackaged(a.Id)), Stopwatch.GetElapsedTime(started));
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    /// <summary>Whether <paramref name="id"/> is a packaged app's (<see cref="AppsFolder.IsPackagedId"/>).</summary>
    public static bool IsPackaged(string id) => AppsFolder.IsPackagedId(id);

    private static List<AppEntry> ReadAppsFolder()
    {
        var folder = (IShellItem)ShellNative.SHGetKnownFolderItem(
            ShellNative.FolderIdAppsFolder, 0, 0, ShellNative.IidShellItem);
        IEnumShellItems? items = null;
        try
        {
            items = (IEnumShellItems)folder.BindToHandler(0, ShellNative.HandlerEnumItems, ShellNative.IidEnumShellItems);

            var apps = new List<AppEntry>();
            while (items.Next(1, out var item, out var fetched) == 0 && fetched == 1 && item is not null)
            {
                try
                {
                    var name = item.GetDisplayName(SIGDN.NormalDisplay);
                    var id = item.GetDisplayName(SIGDN.ParentRelativeParsing);
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                    {
                        apps.Add(new AppEntry(id, name));
                    }
                }
                catch (COMException)
                {
                    // One entry the shell cannot describe is skipped; the rest of the Start
                    // menu is still worth listing.
                }
                finally
                {
                    Marshal.FinalReleaseComObject(item);
                }
            }

            apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return apps;
        }
        finally
        {
            if (items is not null)
            {
                Marshal.FinalReleaseComObject(items);
            }

            Marshal.FinalReleaseComObject(folder);
        }
    }

    private sealed class Snapshot(IReadOnlyList<AppEntry> apps)
    {
        public static Snapshot Empty { get; } = new([]);

        public IReadOnlyList<AppEntry> Apps { get; } = apps;

        public HashSet<string> Ids { get; } = apps.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
