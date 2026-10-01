using Hail.Core.Ports;

namespace Hail.Core.Updates;

/// <summary>Where the updater stands, for the settings window to draw.</summary>
public abstract record UpdateStatus
{
    private UpdateStatus()
    {
    }

    /// <summary>Nothing under way. <paramref name="Message"/> is the last answer worth showing, or null.</summary>
    public sealed record Idle(string? Message) : UpdateStatus;

    /// <summary>GitHub is being asked.</summary>
    public sealed record Checking : UpdateStatus;

    /// <summary>A newer release is on offer. <paramref name="Message"/> says why the last try at it failed, or is null.</summary>
    public sealed record Offering(LatestRelease Release, string? Message) : UpdateStatus;

    /// <summary>The installer is arriving; <paramref name="Fraction"/> runs from 0 to 1.</summary>
    public sealed record Downloading(LatestRelease Release, double Fraction) : UpdateStatus;

    /// <summary>The installer has been started and Hail is about to quit so it can be replaced.</summary>
    public sealed record Installing(LatestRelease Release) : UpdateStatus;
}

/// <summary>
/// The update check's rules, from Sling's (Hail.md §9, §12 M3): nothing is sent until the user has
/// said yes; then at most once a day, when Hail starts or is summoned, never on a timer; a failed
/// automatic check says nothing and waits an hour; an offer can be installed, left for later or
/// skipped; the installer is verified twice and runs visibly.
/// </summary>
/// <remarks>
/// Call it from one thread (the UI's). Its awaits resume on that thread's context, so
/// <see cref="StatusChanged"/> and <see cref="Offered"/> are raised there too.
/// </remarks>
public sealed class UpdateCoordinator
{
    private readonly IUpdateService _service;
    private readonly IUpdateStateStore _store;
    private readonly TimeProvider _time;

    private UpdateState _state = UpdateState.Empty;
    private CancellationTokenSource? _work;
    private DateTimeOffset? _lastAutomaticAttempt;
    private bool? _allowed;
    private bool _loaded;
    private bool _changedBeforeLoad;

    public UpdateCoordinator(IUpdateService service, IUpdateStateStore store, ReleaseVersion running, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(running);
        _service = service;
        _store = store;
        _time = time ?? TimeProvider.System;
        Running = running;
    }

    /// <summary>The status changed; read <see cref="Status"/>.</summary>
    public event Action? StatusChanged;

    /// <summary>An automatic check found a release worth offering; the host says so once.</summary>
    public event Action<LatestRelease>? Offered;

    /// <summary>The version running now.</summary>
    public ReleaseVersion Running { get; }

    public UpdateStatus Status { get; private set; } = new UpdateStatus.Idle(null);

    /// <summary>True while GitHub is being asked or an installer is arriving or starting.</summary>
    public bool IsBusy => _work is not null || Status is UpdateStatus.Installing;

    /// <summary>
    /// Whether the user has said Hail may check: null until they answer. Switching it off stops a
    /// check in flight and takes down an offer; a download is left alone, since somebody pressed
    /// Update for that one and it has its own Cancel.
    /// </summary>
    public bool? Allowed
    {
        get => _allowed;
        set
        {
            _allowed = value;
            if (value == true)
            {
                return;
            }

            if (Status is UpdateStatus.Checking)
            {
                _work?.Cancel();
            }

            if (Status is UpdateStatus.Offering)
            {
                SetStatus(new UpdateStatus.Idle(null));
            }
        }
    }

    /// <summary>True when Hail has never asked whether it may check, and the user has not said.</summary>
    public bool ShouldAskPermission => _allowed is null && _state.AskedUtc is null;

    /// <summary>Reads what earlier runs remembered, off the caller's thread.</summary>
    /// <remarks>
    /// If the user checked or skipped before it finished, what they did this session is newer
    /// than the file and is kept.
    /// </remarks>
    public async Task LoadAsync()
    {
        var stored = await Task.Run(_store.Load).ConfigureAwait(true);
        if (!_changedBeforeLoad)
        {
            _state = stored;
        }

        _loaded = true;
    }

    /// <summary>Reads what earlier runs remembered, on this thread.</summary>
    public void Load()
    {
        _state = _store.Load();
        _loaded = true;
    }

    /// <summary>Records that Hail has asked, so it never asks again.</summary>
    public void MarkAsked()
    {
        Remember(_state with { AskedUtc = _time.GetUtcNow() });
    }

    /// <summary>Checks if the user allows it, a day has passed, and nothing else is under way. Quiet on failure.</summary>
    public async Task CheckIfDueAsync()
    {
        var now = _time.GetUtcNow();
        if (_allowed != true
            || IsBusy
            || Status is not UpdateStatus.Idle
            || !UpdateSchedule.IsDue(_state.LastCheckedUtc, now)
            || (_lastAutomaticAttempt is { } attempt && now - attempt < UpdateSchedule.RetryAfterFailure))
        {
            return;
        }

        _lastAutomaticAttempt = now;
        await CheckAsync(userAsked: false).ConfigureAwait(true);
    }

    /// <summary>
    /// "Check now": asks whatever the schedule says, offers a skipped version again, and says what
    /// it found. Works whether or not automatic checks are allowed: pressing it is the permission.
    /// </summary>
    public async Task CheckNowAsync()
    {
        if (IsBusy)
        {
            return;
        }

        await CheckAsync(userAsked: true).ConfigureAwait(true);
    }

    /// <summary>
    /// Downloads the offered release, verifies it, and starts its installer.
    /// </summary>
    /// <returns>True when the installer is running and Hail should quit so it can be replaced.</returns>
    public async Task<bool> InstallAsync()
    {
        if (Status is not UpdateStatus.Offering offering || IsBusy)
        {
            return false;
        }

        var release = offering.Release;
        DownloadedInstaller installer;
        using (var work = new CancellationTokenSource())
        {
            _work = work;
            SetStatus(new UpdateStatus.Downloading(release, 0));
            var progress = new Progress<double>(fraction =>
            {
                if (Status is UpdateStatus.Downloading downloading && downloading.Release == release)
                {
                    SetStatus(downloading with { Fraction = Math.Clamp(fraction, 0, 1) });
                }
            });

            try
            {
                installer = await _service.DownloadAsync(release, progress, work.Token).ConfigureAwait(true);
            }
            catch (UpdateCheckException ex)
            {
                SetStatus(new UpdateStatus.Offering(release, ex.Message));
                return false;
            }
            catch (OperationCanceledException)
            {
                SetStatus(new UpdateStatus.Offering(release, "The download was stopped. Nothing was changed."));
                return false;
            }
            catch
            {
                // Anything unforeseen: the offer stands, so the card is never left saying
                // "Downloading" with nothing to cancel; the caller logs what it was.
                SetStatus(new UpdateStatus.Offering(release, "The download failed. Try again."));
                throw;
            }
            finally
            {
                _work = null;
            }
        }

        SetStatus(new UpdateStatus.Installing(release));
        try
        {
            // Hashing the installer again reads it whole; not on the caller's thread.
            await Task.Run(() => _service.Launch(installer)).ConfigureAwait(true);
        }
        catch (UpdateCheckException ex)
        {
            _service.Discard(installer);
            SetStatus(new UpdateStatus.Offering(release, ex.Message));
            return false;
        }
        catch
        {
            _service.Discard(installer);
            SetStatus(new UpdateStatus.Offering(release, "The installer could not be started. Try again."));
            throw;
        }

        return true;
    }

    /// <summary>Stops a download under way; the partial file is deleted and the offer stands.</summary>
    public void CancelDownload()
    {
        if (Status is UpdateStatus.Downloading)
        {
            _work?.Cancel();
        }
    }

    /// <summary>"Later": the offer goes, and comes back at tomorrow's check.</summary>
    public void Later()
    {
        if (Status is UpdateStatus.Offering)
        {
            SetStatus(new UpdateStatus.Idle(null));
        }
    }

    /// <summary>"Skip this version": not offered again by an automatic check; Check now still offers it.</summary>
    public void Skip()
    {
        if (Status is not UpdateStatus.Offering offering)
        {
            return;
        }

        var version = offering.Release.Version.ToString();
        Remember(_state with { SkippedVersion = version });
        SetStatus(new UpdateStatus.Idle($"Hail {version} skipped. Check now offers it again."));
    }

    private async Task CheckAsync(bool userAsked)
    {
        using var work = new CancellationTokenSource();
        _work = work;
        var before = Status;
        SetStatus(new UpdateStatus.Checking());

        try
        {
            await Task.Run(_service.SweepOldDownloads, work.Token).ConfigureAwait(true);
            var latest = await _service.GetLatestAsync(work.Token).ConfigureAwait(true);

            Remember(_state with { LastCheckedUtc = _time.GetUtcNow() });

            if (UpdateSchedule.ShouldOffer(Running, latest.Version, _state.SkippedVersion, userAsked))
            {
                SetStatus(new UpdateStatus.Offering(latest, null));
                if (!userAsked)
                {
                    Offered?.Invoke(latest);
                }
            }
            else
            {
                SetStatus(new UpdateStatus.Idle(userAsked
                    ? latest.Version >= Running
                        ? $"Hail {Running} is the newest version."
                        : $"Hail {Running} is newer than the newest release ({latest.Version})."
                    : null));
            }
        }
        catch (UpdateCheckException ex)
        {
            SetStatus(new UpdateStatus.Idle(userAsked ? ex.Message : (before as UpdateStatus.Idle)?.Message));
        }
        catch (OperationCanceledException)
        {
            // Switched off while asking: nothing to say.
            SetStatus(new UpdateStatus.Idle(null));
        }
        catch
        {
            SetStatus(new UpdateStatus.Idle(userAsked ? "The check failed. Try again later." : null));
            throw;
        }
        finally
        {
            _work = null;
        }
    }

    private void Remember(UpdateState state)
    {
        _state = state;
        _changedBeforeLoad |= !_loaded;
        _store.Save(state);
    }

    private void SetStatus(UpdateStatus status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }
}
