using Hail.Core.Hosting;
using Hail.Core.Matching;
using Hail.Core.Ports;
using Hail.Providers.Apps;
using Hail.Sdk;

namespace Hail.Providers.Tests;

public sealed class AppsProviderTests
{
    private readonly FakeCatalog _catalog = new(
        new AppEntry("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Calculator"),
        new AppEntry(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Microsoft VS Code\Code.exe", "Visual Studio Code"),
        new AppEntry("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", "Notepad"));

    private readonly FakeLauncher _launcher = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<AppsProvider> StartedAsync()
    {
        var provider = new AppsProvider(_catalog);
        await provider.InitializeAsync(new PluginContext(AppsProvider.ProviderId, _launcher, new FuzzyMatcher(), new NullLog()), Token);
        return provider;
    }

    private static async Task<List<Result>> CollectAsync(AppsProvider provider, string text)
    {
        var results = new List<Result>();
        await foreach (var result in provider.QueryAsync(Query.Global(text), TestContext.Current.CancellationToken))
        {
            results.Add(result);
        }

        return results;
    }

    [Fact]
    public async Task Finds_apps_by_name()
    {
        var results = await CollectAsync(await StartedAsync(), "vsc");

        var result = Assert.Single(results);
        Assert.Equal("Visual Studio Code", result.Title);
        Assert.Equal([0, 7, 14], result.Highlight!.Positions());
    }

    [Fact]
    public async Task An_empty_box_lists_nothing()
    {
        Assert.Empty(await CollectAsync(await StartedAsync(), "   "));
    }

    [Fact]
    public async Task The_id_is_the_apps_own_and_the_icon_is_the_shells()
    {
        var result = Assert.Single(await CollectAsync(await StartedAsync(), "calc"));

        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", result.Id);
        Assert.Equal(
            IconSource.ForShellItem(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
            result.Icon);
    }

    [Fact]
    public async Task Enter_launches_the_app_by_its_id_and_hides_the_box()
    {
        var result = Assert.Single(await CollectAsync(await StartedAsync(), "note"));

        Assert.Equal(Gesture.Enter, result.Primary.Gesture);
        var outcome = await result.Primary.Execute(new ActionContext(Query.Global("note")), Token);

        Assert.Equal(ActionOutcome.Hide, outcome);
        Assert.Equal(["Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"], _launcher.Launched);
    }

    [Fact]
    public async Task A_refresh_of_the_catalog_is_seen_by_the_next_query()
    {
        var provider = await StartedAsync();
        Assert.Empty(await CollectAsync(provider, "paint"));

        _catalog.Replace(new AppEntry("Microsoft.Paint_8wekyb3d8bbwe!App", "Paint"));

        Assert.Single(await CollectAsync(provider, "paint"));
    }

    [Fact]
    public async Task A_query_before_initialisation_is_a_bug_said_plainly()
    {
        var provider = new AppsProvider(_catalog);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(provider, "calc"));
    }

    [Fact]
    public async Task Cancellation_stops_the_scan()
    {
        var provider = await StartedAsync();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in provider.QueryAsync(Query.Global("a"), cancel.Token))
            {
            }
        });
    }

    private sealed class FakeCatalog(params AppEntry[] apps) : IAppCatalog
    {
        public IReadOnlyList<AppEntry> Apps { get; private set; } = apps;

        public void Replace(params AppEntry[] apps) => Apps = apps;
    }

    private sealed class FakeLauncher : ILauncher
    {
        public List<string> Launched { get; } = [];

        public ValueTask LaunchAppAsync(string appId, CancellationToken ct)
        {
            Launched.Add(appId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullLog : IHostLog
    {
        public void LogInfo(string message)
        {
        }

        public void LogError(string message, Exception? exception = null)
        {
        }
    }
}
