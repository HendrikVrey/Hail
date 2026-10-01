using Hail.Core.Settings;
using Hail.Core.Updates;
using Hail.Persistence;
using Hail.Updates;

namespace Hail.App;

/// <summary>
/// The update check, from the host's side (Hail.md §9, §12 M3). Nothing is sent until the user
/// says yes; Hail asks once, in a notification, shortly after its first start with this code.
/// Then at most once a day, a little after Hail starts or when the box is summoned, and never on
/// a timer. An offer is a notification that opens the settings window, where the update is
/// installed, left or skipped.
/// </summary>
internal sealed partial class HailHost
{
    /// <summary>Sign-in is busy; the question waits until it has settled.</summary>
    private static readonly TimeSpan FirstAskDelay = TimeSpan.FromSeconds(30);

    /// <summary>Likewise the first check of a session.</summary>
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);

    private UpdateService? _updateService;
    private UpdateCoordinator? _updates;
    private bool _updatesLoaded;

    public async Task InstallUpdateAsync()
    {
        if (_updates is null)
        {
            return;
        }

        var release = (_updates.Status as UpdateStatus.Offering)?.Release;
        bool started;
        try
        {
            started = await _updates.InstallAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The coordinator says its own failures; anything else is logged and the offer stands.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.LogError("Installing an update failed.", ex);
            return;
        }

        if (started)
        {
            log.LogInfo($"The installer for Hail {release} is running; Hail quits so it can be replaced.");
            Quit("an update");
        }
    }

    private void CreateUpdater(HailSettings settings)
    {
        var running = ReleaseVersion.Of(typeof(HailHost).Assembly);
        _updateService = new UpdateService(running);
        _updates = new UpdateCoordinator(_updateService, new UpdateStateStore(paths), running)
        {
            Allowed = settings.CheckForUpdates,
        };

        _updates.Offered += release => Notify(
            $"Hail {release.Version} is available",
            "Click here to see it. Nothing changes until you choose Update.",
            NoticeAction.OpenUpdates,
            warning: false);
    }

    private async Task StartUpdatesAsync()
    {
        try
        {
            await _updates!.LoadAsync().ConfigureAwait(true);
            _updatesLoaded = true;

            if (_updates.ShouldAskPermission)
            {
                await Task.Delay(FirstAskDelay, _lifetime.Token).ConfigureAwait(true);
                if (!_disposed && _updates.ShouldAskPermission)
                {
                    Notify(
                        "Can Hail check for new versions?",
                        "Once a day, Hail can ask GitHub whether a new version is out. Nothing you type is sent. Click here to choose.",
                        NoticeAction.OpenUpdates,
                        warning: false);
                    _updates.MarkAsked();
                }

                return;
            }

            await Task.Delay(FirstCheckDelay, _lifetime.Token).ConfigureAwait(true);
            CheckForUpdatesIfDue();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Quitting.
        }
    }

    /// <summary>Starts an automatic check if one is due; returns at once and never delays the box.</summary>
    private void CheckForUpdatesIfDue()
    {
        if (_updates is null || !_updatesLoaded || _disposed)
        {
            return;
        }

        _ = RunAutomaticCheckAsync();
    }

    private async Task RunAutomaticCheckAsync()
    {
        try
        {
            await _updates!.CheckIfDueAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // An automatic check says nothing when it fails; the log has it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.LogError("An update check failed.", ex);
        }
    }
}
