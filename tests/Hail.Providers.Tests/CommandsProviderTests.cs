using Hail.Core.Ports;
using Hail.Providers.Commands;
using Hail.Sdk;
using static Hail.Providers.Tests.Harness;

namespace Hail.Providers.Tests;

public sealed class CommandsProviderTests
{
    private readonly FakeSession _session = new();
    private readonly FakeHost _host = new();
    private readonly FakeLauncher _launcher = new();

    private async Task<CommandsProvider> StartedAsync()
    {
        var provider = new CommandsProvider(_session, _host);
        await provider.InitializeAsync(Context(_launcher, new FakeClipboard()), Token);
        return provider;
    }

    private async Task<Result> OneAsync(string text) =>
        Assert.Single(await CollectAsync(await StartedAsync(), Query.Global(text)));

    [Fact]
    public async Task Restart_asks_first_and_runs_only_when_confirmed()
    {
        var restart = await OneAsync("restart");

        var outcome = Assert.IsType<ActionOutcome.Confirm>(await Run(restart.Primary));
        Assert.Empty(_session.Calls);
        Assert.Contains("unsaved work", outcome.Question, StringComparison.Ordinal);

        Assert.Equal(ActionOutcome.Hide, await Run(outcome.Confirmed));
        Assert.Equal(["restart"], _session.Calls);
    }

    [Theory]
    [InlineData("sleep", "sleep")]
    [InlineData("sign out", "signout")]
    [InlineData("shut down", "shutdown")]
    public async Task Everything_that_can_cost_work_asks_first(string text, string call)
    {
        var confirm = Assert.IsType<ActionOutcome.Confirm>(await Run((await OneAsync(text)).Primary));
        await Run(confirm.Confirmed);

        Assert.Equal([call], _session.Calls);
    }

    [Fact]
    public async Task Lock_runs_at_once_since_nothing_is_lost()
    {
        Assert.Equal(ActionOutcome.Hide, await Run((await OneAsync("lock")).Primary));
        Assert.Equal(["lock"], _session.Calls);
    }

    [Fact]
    public async Task Another_name_finds_it_without_bolding_a_title_it_is_not_in()
    {
        var result = await OneAsync("reboot");

        Assert.Equal("Restart", result.Title);
        Assert.Null(result.Highlight);
    }

    [Fact]
    public async Task Quit_asks_the_host()
    {
        await Run((await OneAsync("quit hail")).Primary);
        Assert.Equal(1, _host.Quits);
    }

    [Fact]
    public async Task Settings_opens_the_settings_file()
    {
        await Run((await OneAsync("hail settings")).Primary);
        Assert.Equal([$"open {_host.SettingsPath}"], _launcher.Calls);
    }

    [Fact]
    public async Task One_letter_is_not_enough()
    {
        Assert.Empty(await CollectAsync(await StartedAsync(), Query.Global("s")));
    }

    [Fact]
    public async Task A_remembered_command_is_rebuilt_by_its_id()
    {
        var provider = await StartedAsync();

        Assert.Equal("Restart", (await provider.RecallAsync("restart", Token))!.Title);
        Assert.Null(await provider.RecallAsync("empty-recycle-bin", Token));
    }

    [Fact]
    public async Task There_is_no_way_to_empty_the_recycle_bin()
    {
        Assert.Empty(await CollectAsync(await StartedAsync(), Query.Global("empty recycle bin")));
    }

    private sealed class FakeSession : ISessionControl
    {
        public List<string> Calls { get; } = [];

        public void Lock() => Calls.Add("lock");

        public void Sleep() => Calls.Add("sleep");

        public void SignOut() => Calls.Add("signout");

        public void Restart() => Calls.Add("restart");

        public void ShutDown() => Calls.Add("shutdown");
    }

    private sealed class FakeHost : IHostCommands
    {
        public int Quits { get; private set; }

        public string SettingsPath => @"C:\Users\Test\AppData\Local\Hail\settings.json";

        public void Quit() => Quits++;
    }
}
