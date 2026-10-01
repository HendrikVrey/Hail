using Hail.Core.Ports;
using Hail.Core.Updates;

namespace Hail.Core.Tests;

/// <summary>The updater's rules, against a fake GitHub, disk and installer.</summary>
public sealed class UpdateCoordinatorTests
{
    private static readonly ReleaseVersion Running = ReleaseVersion.TryParse("1.0.0")!;

    private readonly FakeService _service = new();
    private readonly FakeStore _store = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Nothing_is_sent_until_the_user_says_yes()
    {
        var updates = Coordinator(allowed: null);

        await updates.CheckIfDueAsync();

        Assert.Equal(0, _service.Checks);
        Assert.True(updates.ShouldAskPermission);
    }

    [Fact]
    public async Task Nothing_is_sent_when_the_user_said_no()
    {
        var updates = Coordinator(allowed: false);

        await updates.CheckIfDueAsync();

        Assert.Equal(0, _service.Checks);
        Assert.False(updates.ShouldAskPermission);
    }

    [Fact]
    public void Hail_asks_once()
    {
        var updates = Coordinator(allowed: null);
        Assert.True(updates.ShouldAskPermission);

        updates.MarkAsked();

        Assert.False(updates.ShouldAskPermission);
        Assert.False(Coordinator(allowed: null).ShouldAskPermission);
        Assert.Equal(_time.Now, _store.State.AskedUtc);
    }

    [Fact]
    public async Task A_newer_release_is_offered_and_announced_once()
    {
        _service.Latest = Release("1.1.0");
        var updates = Coordinator(allowed: true);
        var announced = new List<LatestRelease>();
        updates.Offered += announced.Add;

        await updates.CheckIfDueAsync();
        await updates.CheckIfDueAsync();

        var offering = Assert.IsType<UpdateStatus.Offering>(updates.Status);
        Assert.Equal("1.1.0", offering.Release.Version.ToString());
        Assert.Single(announced);
        Assert.Equal(1, _service.Checks);
        Assert.Equal(_time.Now, _store.State.LastCheckedUtc);
    }

    [Fact]
    public async Task At_most_one_automatic_check_a_day()
    {
        _service.Latest = Release("1.0.0");
        var updates = Coordinator(allowed: true);

        await updates.CheckIfDueAsync();
        _time.Now += TimeSpan.FromHours(23);
        await updates.CheckIfDueAsync();
        Assert.Equal(1, _service.Checks);

        _time.Now += TimeSpan.FromHours(1);
        await updates.CheckIfDueAsync();
        Assert.Equal(2, _service.Checks);
    }

    [Fact]
    public async Task A_failed_automatic_check_says_nothing_and_waits_an_hour()
    {
        _service.Failure = new UpdateCheckException("GitHub is down.");
        var updates = Coordinator(allowed: true);

        await updates.CheckIfDueAsync();
        Assert.Equal(new UpdateStatus.Idle(null), updates.Status);

        _time.Now += TimeSpan.FromMinutes(59);
        await updates.CheckIfDueAsync();
        Assert.Equal(1, _service.Checks);

        _time.Now += TimeSpan.FromMinutes(1);
        await updates.CheckIfDueAsync();
        Assert.Equal(2, _service.Checks);
    }

    [Fact]
    public async Task Check_now_says_what_it_found_and_ignores_the_schedule()
    {
        _service.Latest = Release("1.0.0");
        var updates = Coordinator(allowed: false);

        await updates.CheckNowAsync();
        await updates.CheckNowAsync();

        Assert.Equal(2, _service.Checks);
        Assert.Equal(new UpdateStatus.Idle("Hail 1.0.0 is the newest version."), updates.Status);
    }

    [Fact]
    public async Task Check_now_says_why_it_failed()
    {
        _service.Failure = new UpdateCheckException("No version of Hail has been released yet.");
        var updates = Coordinator(allowed: true);

        await updates.CheckNowAsync();

        Assert.Equal(new UpdateStatus.Idle("No version of Hail has been released yet."), updates.Status);
    }

    [Fact]
    public async Task A_rolling_build_is_told_it_is_ahead()
    {
        _service.Latest = Release("0.9.0");
        var updates = Coordinator(allowed: true);

        await updates.CheckNowAsync();

        Assert.Equal(new UpdateStatus.Idle("Hail 1.0.0 is newer than the newest release (0.9.0)."), updates.Status);
    }

    [Fact]
    public async Task A_skipped_version_is_not_announced_again_but_check_now_offers_it()
    {
        _service.Latest = Release("1.1.0");
        var updates = Coordinator(allowed: true);
        var announced = 0;
        updates.Offered += _ => announced++;
        await updates.CheckIfDueAsync();

        updates.Skip();
        Assert.Equal("1.1.0", _store.State.SkippedVersion);
        Assert.IsType<UpdateStatus.Idle>(updates.Status);

        _time.Now += TimeSpan.FromDays(1);
        await updates.CheckIfDueAsync();
        Assert.IsType<UpdateStatus.Idle>(updates.Status);
        Assert.Equal(1, announced);

        await updates.CheckNowAsync();
        Assert.IsType<UpdateStatus.Offering>(updates.Status);
    }

    [Fact]
    public async Task Later_takes_the_offer_down_until_the_next_days_check()
    {
        _service.Latest = Release("1.1.0");
        var updates = Coordinator(allowed: true);
        await updates.CheckIfDueAsync();

        updates.Later();
        Assert.Equal(new UpdateStatus.Idle(null), updates.Status);

        _time.Now += TimeSpan.FromDays(1);
        await updates.CheckIfDueAsync();
        Assert.IsType<UpdateStatus.Offering>(updates.Status);
    }

    [Fact]
    public async Task Switching_checks_off_takes_down_an_offer()
    {
        _service.Latest = Release("1.1.0");
        var updates = Coordinator(allowed: true);
        await updates.CheckIfDueAsync();

        updates.Allowed = false;

        Assert.Equal(new UpdateStatus.Idle(null), updates.Status);
    }

    [Fact]
    public async Task Switching_checks_off_stops_a_check_under_way()
    {
        var answer = new TaskCompletionSource<LatestRelease>();
        _service.Answer = ct =>
        {
            ct.Register(() => answer.TrySetCanceled(ct));
            return answer.Task;
        };
        var updates = Coordinator(allowed: true);

        var checking = updates.CheckIfDueAsync();
        Assert.IsType<UpdateStatus.Checking>(updates.Status);
        updates.Allowed = false;
        await checking;

        Assert.Equal(new UpdateStatus.Idle(null), updates.Status);
        Assert.Null(_store.State.LastCheckedUtc);
    }

    [Fact]
    public async Task Install_downloads_then_launches_and_says_hail_should_quit()
    {
        _service.Latest = Release("1.1.0");
        var updates = Coordinator(allowed: true);
        await updates.CheckIfDueAsync();

        Assert.True(await updates.InstallAsync());

        Assert.IsType<UpdateStatus.Installing>(updates.Status);
        Assert.Equal(["sweep", "download 1.1.0", "launch"], _service.Calls);
    }

    [Fact]
    public async Task A_failed_download_keeps_the_offer_and_says_why()
    {
        _service.Latest = Release("1.1.0");
        _service.DownloadFailure = new UpdateCheckException("The download stalled, so it was stopped. Try again.");
        var updates = Coordinator(allowed: true);
        await updates.CheckIfDueAsync();

        Assert.False(await updates.InstallAsync());

        var offering = Assert.IsType<UpdateStatus.Offering>(updates.Status);
        Assert.Equal("The download stalled, so it was stopped. Try again.", offering.Message);
        Assert.DoesNotContain("launch", _service.Calls);
    }

    [Fact]
    public async Task An_installer_that_changed_after_the_check_is_discarded_and_not_run()
    {
        _service.Latest = Release("1.1.0");
        _service.LaunchFailure = new UpdateCheckException("The installer changed on disk after it was checked, so it was not run.");
        var updates = Coordinator(allowed: true);
        await updates.CheckIfDueAsync();

        Assert.False(await updates.InstallAsync());

        Assert.IsType<UpdateStatus.Offering>(updates.Status);
        Assert.Contains("discard", _service.Calls);
    }

    [Fact]
    public async Task Cancelling_a_download_keeps_the_offer()
    {
        _service.Latest = Release("1.1.0");
        var download = new TaskCompletionSource<DownloadedInstaller>();
        _service.Download = ct =>
        {
            ct.Register(() => download.TrySetCanceled(ct));
            return download.Task;
        };
        var updates = Coordinator(allowed: true);
        await updates.CheckIfDueAsync();

        var installing = updates.InstallAsync();
        Assert.IsType<UpdateStatus.Downloading>(updates.Status);
        updates.CancelDownload();

        Assert.False(await installing);
        Assert.Contains("stopped", Assert.IsType<UpdateStatus.Offering>(updates.Status).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_else_starts_while_a_download_is_under_way()
    {
        _service.Latest = Release("1.1.0");
        var download = new TaskCompletionSource<DownloadedInstaller>();
        _service.Download = _ => download.Task;
        var updates = Coordinator(allowed: true);
        await updates.CheckIfDueAsync();

        var installing = updates.InstallAsync();
        await updates.CheckNowAsync();
        Assert.False(await updates.InstallAsync());
        Assert.Equal(1, _service.Checks);

        download.SetResult(new DownloadedInstaller("setup.exe", "folder", new byte[32]));
        Assert.True(await installing);
    }

    [Fact]
    public async Task An_unforeseen_failure_in_a_download_leaves_the_offer_not_a_stuck_download()
    {
        _service.Latest = Release("1.1.0");
        _service.Download = _ => Task.FromException<DownloadedInstaller>(new UnauthorizedAccessException("denied"));
        var updates = Coordinator(allowed: true);
        await updates.CheckIfDueAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(updates.InstallAsync);

        Assert.IsType<UpdateStatus.Offering>(updates.Status);
        Assert.False(updates.IsBusy);
    }

    [Fact]
    public async Task An_unforeseen_failure_in_a_check_leaves_it_idle_and_checks_go_on()
    {
        _service.Answer = _ => Task.FromException<LatestRelease>(new ObjectDisposedException("client"));
        var updates = Coordinator(allowed: true);

        await Assert.ThrowsAsync<ObjectDisposedException>(updates.CheckNowAsync);

        Assert.IsType<UpdateStatus.Idle>(updates.Status);
        Assert.False(updates.IsBusy);
    }

    [Fact]
    public async Task What_the_user_did_before_the_file_was_read_is_not_overwritten_by_it()
    {
        _store.Seed(new UpdateState(null, "0.9.0", null));
        _service.Latest = Release("1.1.0");
        var updates = new UpdateCoordinator(_service, _store, Running, _time) { Allowed = true };

        // Check now before the file has been read: its answer is this session's, and newer.
        await updates.CheckNowAsync();
        await updates.LoadAsync();

        Assert.Equal(_time.Now, _store.State.LastCheckedUtc);
        updates.Skip();
        Assert.Equal("1.1.0", _store.State.SkippedVersion);
    }

    [Fact]
    public async Task Old_downloads_are_swept_before_each_check()
    {
        _service.Latest = Release("1.0.0");
        var updates = Coordinator(allowed: true);

        await updates.CheckNowAsync();

        Assert.Equal("sweep", _service.Calls[0]);
    }

    private UpdateCoordinator Coordinator(bool? allowed)
    {
        var coordinator = new UpdateCoordinator(_service, _store, Running, _time) { Allowed = allowed };
        coordinator.Load();
        return coordinator;
    }

    private static LatestRelease Release(string version) =>
        new(ReleaseVersion.TryParse(version)!, new Uri($"https://github.com/HendrikVrey/Hail/releases/download/v{version}/Hail-Setup.exe"), 1000, new byte[32]);

    private sealed class FakeService : IUpdateService
    {
        public LatestRelease? Latest { get; set; }

        public UpdateCheckException? Failure { get; set; }

        public UpdateCheckException? DownloadFailure { get; set; }

        public UpdateCheckException? LaunchFailure { get; set; }

        public Func<CancellationToken, Task<LatestRelease>>? Answer { get; set; }

        public Func<CancellationToken, Task<DownloadedInstaller>>? Download { get; set; }

        public int Checks { get; private set; }

        public List<string> Calls { get; } = [];

        public Task<LatestRelease> GetLatestAsync(CancellationToken ct)
        {
            Checks++;
            if (Answer is not null)
            {
                return Answer(ct);
            }

            return Failure is null ? Task.FromResult(Latest!) : Task.FromException<LatestRelease>(Failure);
        }

        public Task<DownloadedInstaller> DownloadAsync(LatestRelease release, IProgress<double>? progress, CancellationToken ct)
        {
            Calls.Add($"download {release.Version}");
            if (Download is not null)
            {
                return Download(ct);
            }

            progress?.Report(1);
            return DownloadFailure is null
                ? Task.FromResult(new DownloadedInstaller("setup.exe", "folder", release.Sha256))
                : Task.FromException<DownloadedInstaller>(DownloadFailure);
        }

        public void Launch(DownloadedInstaller installer)
        {
            if (LaunchFailure is not null)
            {
                throw LaunchFailure;
            }

            Calls.Add("launch");
        }

        public void Discard(DownloadedInstaller installer) => Calls.Add("discard");

        public void SweepOldDownloads() => Calls.Add("sweep");
    }

    private sealed class FakeStore : IUpdateStateStore
    {
        public UpdateState State { get; private set; } = UpdateState.Empty;

        public UpdateState Load() => State;

        public void Seed(UpdateState state) => State = state;

        public bool Save(UpdateState state)
        {
            State = state;
            return true;
        }
    }
}
