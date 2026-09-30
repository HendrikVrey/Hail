using Hail.Core.Ports;
using Hail.Providers.Apps;
using Hail.Sdk;
using static Hail.Providers.Tests.Harness;

namespace Hail.Providers.Tests;

public sealed class AppsProviderTests
{
    private const string Calculator = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
    private const string Code = @"{6D809377-6AF0-444B-8957-A3773F02200E}\Microsoft VS Code\Code.exe";
    private const string Notepad = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App";

    private readonly FakeCatalog _catalog = new(
        new AppEntry(Calculator, "Calculator"),
        new AppEntry(Code, "Visual Studio Code"),
        new AppEntry(Notepad, "Notepad"));

    private readonly FakeLauncher _launcher = new();

    private async Task<AppsProvider> StartedAsync()
    {
        var provider = new AppsProvider(_catalog);
        await provider.InitializeAsync(Context(_launcher, new FakeClipboard()), Token);
        return provider;
    }

    private static Task<List<Result>> FindAsync(AppsProvider provider, string text) => CollectAsync(provider, Query.Global(text));

    [Fact]
    public async Task Finds_apps_by_name()
    {
        var result = Assert.Single(await FindAsync(await StartedAsync(), "vsc"));

        Assert.Equal("Visual Studio Code", result.Title);
        Assert.Equal([0, 7, 14], result.Highlight!.Positions());
    }

    [Fact]
    public async Task An_empty_box_lists_nothing()
    {
        Assert.Empty(await FindAsync(await StartedAsync(), "   "));
    }

    [Fact]
    public async Task The_id_is_the_apps_own_and_the_icon_is_the_shells()
    {
        var result = Assert.Single(await FindAsync(await StartedAsync(), "calc"));

        Assert.Equal(Calculator, result.Id);
        Assert.Equal(IconSource.ForShellItem($@"shell:AppsFolder\{Calculator}"), result.Icon);
    }

    [Fact]
    public async Task Enter_launches_the_app_by_its_id_and_hides_the_box()
    {
        var result = Assert.Single(await FindAsync(await StartedAsync(), "note"));

        Assert.Equal(ActionOutcome.Hide, await Run(result.Primary));
        Assert.Equal([$"launch {Notepad}"], _launcher.Calls);
    }

    [Fact]
    public async Task A_desktop_app_offers_to_run_as_administrator()
    {
        var result = Assert.Single(await FindAsync(await StartedAsync(), "vsc"));

        await Run(On(result, Gesture.CtrlShiftEnter));

        Assert.Equal([$"elevate {Code}"], _launcher.Calls);
    }

    [Fact]
    public async Task A_packaged_app_does_not()
    {
        Assert.Empty(Assert.Single(await FindAsync(await StartedAsync(), "calc")).Secondary);
    }

    [Fact]
    public async Task A_refresh_of_the_catalog_is_seen_by_the_next_query()
    {
        var provider = await StartedAsync();
        Assert.Empty(await FindAsync(provider, "paint"));

        _catalog.Replace(new AppEntry("Microsoft.Paint_8wekyb3d8bbwe!App", "Paint"));

        Assert.Single(await FindAsync(provider, "paint"));
    }

    [Fact]
    public async Task A_remembered_app_is_rebuilt_from_its_id()
    {
        var provider = await StartedAsync();

        var found = Assert.IsType<Recollection.Found>(await provider.RecallAsync(Notepad.ToUpperInvariant(), Token));

        Assert.Equal("Notepad", found.Result.Title);
        Assert.Equal(Recollection.Gone, await provider.RecallAsync("Uninstalled.App_0!App", Token));
    }

    [Fact]
    public async Task Before_the_start_menu_is_read_nothing_is_said_to_be_gone()
    {
        var provider = await StartedAsync();
        _catalog.Replace();

        Assert.Equal(Recollection.Unknown, await provider.RecallAsync(Notepad, Token));
    }

    [Fact]
    public async Task A_query_before_initialisation_is_a_bug_said_plainly()
    {
        var provider = new AppsProvider(_catalog);
        await Assert.ThrowsAsync<InvalidOperationException>(() => FindAsync(provider, "calc"));
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
}
