using System.Text.Json.Nodes;
using Hail.Core.Hosting;
using Hail.Core.Plugins;
using Hail.Sdk;

namespace Hail.Plugins.Tests;

/// <summary>
/// The plugin host against real plugins (Hail.md §6.3, §9, §13): found and left off until the
/// user answers, loaded on first use from the bytes that were approved, isolated from each
/// other, refused with a sentence when anything is wrong, and unloaded again.
/// </summary>
public sealed class PluginLoadingTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    private readonly PluginBench _bench = new();

    public void Dispose() => _bench.Dispose();

    [Fact]
    public void A_new_plugin_is_found_and_stays_off_until_the_user_answers()
    {
        _bench.InstallAlpha();

        var scan = _bench.Scan(_bench.Manager());

        var entry = Assert.Single(scan.Entries);
        Assert.Equal(PluginStatus.New, entry.Status);
        Assert.Null(entry.Provider);
        Assert.Empty(scan.Registrations);
    }

    [Fact]
    public async Task An_enabled_plugin_loads_on_its_first_query_and_answers()
    {
        _bench.InstallAlpha();
        _bench.EnableAll();

        var scan = _bench.Scan(_bench.Manager());
        var entry = Assert.Single(scan.Entries);
        Assert.Equal(PluginStatus.Enabled, entry.Status);
        Assert.False(entry.Provider!.IsLoaded);

        Assert.Equal(["alpha one cats"], await PluginBench.QueryAsync(scan.Registrations, "t cats"));
        Assert.True(entry.Provider.IsLoaded);
        Assert.True(entry.Provider.Recalls);
    }

    [Fact]
    public async Task Two_plugins_each_see_their_own_version_of_a_library_they_share_a_name_with()
    {
        _bench.InstallAlpha();
        _bench.InstallBeta();
        _bench.EnableAll();

        var scan = _bench.Scan(_bench.Manager());

        Assert.Equal(["alpha one x", "beta two"], await PluginBench.QueryAsync(scan.Registrations, "x"));
    }

    [Fact]
    public void A_file_changed_after_enabling_turns_the_plugin_off_until_it_is_answered_again()
    {
        var folder = _bench.InstallAlpha();
        _bench.EnableAll();

        File.AppendAllText(Path.Combine(folder, "Hail.TestShared.dll"), "tampered");
        var scan = _bench.Scan(_bench.Manager());

        Assert.Equal(PluginStatus.Changed, Assert.Single(scan.Entries).Status);
        Assert.Empty(scan.Registrations);
    }

    [Fact]
    public void A_file_added_after_enabling_counts_as_a_change()
    {
        var folder = _bench.InstallAlpha();
        _bench.EnableAll();

        File.WriteAllText(Path.Combine(folder, "extra.txt"), "new");

        Assert.Equal(PluginStatus.Changed, Assert.Single(_bench.Scan(_bench.Manager()).Entries).Status);
    }

    [Fact]
    public void A_plugin_kept_off_is_not_asked_about_again_until_its_files_change()
    {
        var folder = _bench.InstallAlpha();
        var manager = _bench.Manager();
        manager.Answer(Assert.Single(_bench.Scan(manager).Entries), enabled: false);

        Assert.Equal(PluginStatus.Declined, Assert.Single(_bench.Scan(_bench.Manager()).Entries).Status);

        File.WriteAllText(Path.Combine(folder, "extra.txt"), "a new version");
        Assert.Equal(PluginStatus.New, Assert.Single(_bench.Scan(_bench.Manager()).Entries).Status);
    }

    [Fact]
    public async Task An_assembly_changed_between_the_scan_and_the_first_query_is_not_loaded()
    {
        var folder = _bench.InstallAlpha();
        _bench.EnableAll();
        var scan = _bench.Scan(_bench.Manager());

        // After the fingerprint was checked, before a query loads it: the loader checks the bytes it loads.
        File.AppendAllText(Path.Combine(folder, "Hail.TestPlugin.Alpha.dll"), "tampered");

        Assert.Empty(await PluginBench.QueryAsync(scan.Registrations, "t x"));
        var provider = Assert.Single(scan.Entries).Provider!;
        Assert.False(provider.IsLoaded);
        Assert.Contains("changed since", provider.LoadProblem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_added_between_the_scan_and_the_first_query_stops_the_load()
    {
        var folder = _bench.InstallAlpha();
        _bench.EnableAll();
        var scan = _bench.Scan(_bench.Manager());

        // Loading is lazy; a DLL dropped in meanwhile could be one Windows loads for a native library.
        File.WriteAllText(Path.Combine(folder, "version.dll"), "not approved");

        Assert.Empty(await PluginBench.QueryAsync(scan.Registrations, "t x"));
        Assert.Equal("Its files have changed since it was enabled.", Assert.Single(scan.Entries).Provider!.LoadProblem);
    }

    [Fact]
    public async Task Settings_saved_from_a_form_opened_before_a_reload_reach_the_plugin_in_force()
    {
        _bench.InstallAlpha(edit: m => m["settings"] = new JsonArray(
            new JsonObject { ["key"] = "greeting", ["type"] = "text", ["label"] = "Greeting", ["default"] = "hello" },
            new JsonObject { ["key"] = "token", ["type"] = "secret", ["label"] = "Token" }));
        _bench.EnableAll();
        var manager = _bench.Manager();
        var before = manager.Adopt(_bench.Scan(manager)) is var _ ? manager.Entries.Single() : null!;

        var reloaded = _bench.Scan(manager);
        manager.Adopt(reloaded);
        manager.SaveSettings(before, new Dictionary<string, SettingValue> { ["greeting"] = new SettingValue.Text("howzit") });

        Assert.Contains("greeting howzit", await PluginBench.QueryAsync(reloaded.Registrations, "t x"));
    }

    [Theory]
    [InlineData("1.9", "newer Hail")]
    [InlineData("2.0", "newer Hail")]
    [InlineData("0.2", "no longer speaks")]
    public void A_plugin_for_another_sdk_is_refused_before_anything_is_loaded(string sdk, string said)
    {
        _bench.InstallAlpha(edit: m => m["sdk"] = sdk);

        var entry = Assert.Single(_bench.Scan(_bench.Manager()).Entries);

        Assert.Equal(PluginStatus.Invalid, entry.Status);
        Assert.Contains(said, entry.Plugin.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_not_named_after_its_plugin_is_refused()
    {
        _bench.Install("tests.alpha", PluginBench.BuiltTestPlugin("Hail.TestPlugin.Alpha"), "Hail.TestPlugin.Alpha.AlphaProvider", "Hail.TestPlugin.Alpha.dll", folderName: "something-else");

        var entry = Assert.Single(_bench.Scan(_bench.Manager()).Entries);

        Assert.Equal(PluginStatus.Invalid, entry.Status);
        Assert.Contains("named after its id", entry.Plugin.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Hail.TestPlugin.Alpha.NotAProvider", "is not a provider")]
    [InlineData("Hail.TestPlugin.Alpha.Missing", "has no type")]
    [InlineData("Hail.TestPlugin.Alpha.ThrowingProvider", "constructor failed")]
    public async Task A_type_that_cannot_be_made_into_a_provider_is_refused_with_a_sentence(string type, string said)
    {
        _bench.InstallAlpha(type: type);
        _bench.EnableAll();
        var scan = _bench.Scan(_bench.Manager());

        Assert.Empty(await PluginBench.QueryAsync(scan.Registrations, "t x"));

        var provider = Assert.Single(scan.Entries).Provider!;
        Assert.Contains(said, provider.LoadProblem, StringComparison.Ordinal);

        // The plugin's own words never reach the log (Hail.md §9).
        Assert.DoesNotContain(_bench.Log.Lines, l => l.Contains("secret words", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_copy_of_the_sdk_in_the_plugins_folder_is_ignored_for_the_hosts_own()
    {
        var folder = _bench.InstallAlpha();
        File.Copy(typeof(IProvider).Assembly.Location, Path.Combine(folder, "Hail.Sdk.dll"));
        _bench.EnableAll();

        var scan = _bench.Scan(_bench.Manager());

        // Were it the plugin's own copy, its provider would not be the host's IProvider at all.
        Assert.Equal(["alpha one x"], await PluginBench.QueryAsync(scan.Registrations, "t x"));
    }

    [Fact]
    public async Task A_plugin_reads_its_settings_and_its_secret_through_the_context()
    {
        _bench.InstallAlpha(edit: m => m["settings"] = new JsonArray(
            new JsonObject { ["key"] = "greeting", ["type"] = "text", ["label"] = "Greeting", ["default"] = "hello" },
            new JsonObject { ["key"] = "token", ["type"] = "secret", ["label"] = "Token" }));
        _bench.EnableAll();

        var manager = _bench.Manager();
        var scan = _bench.Scan(manager);
        Assert.Contains("greeting hello", await PluginBench.QueryAsync(scan.Registrations, "t x"));

        var entry = Assert.Single(scan.Entries);
        manager.SaveSettings(entry, new Dictionary<string, SettingValue>
        {
            ["greeting"] = new SettingValue.Text("howzit"),
            ["token"] = PluginSettings.ProtectSecret(_bench.Protector, entry.Id, "token", "s3cret"),
        });

        // Saved values reach the running plugin at once, and a later start reads them from disk.
        Assert.Equal(["alpha one x", "greeting howzit", "secret s3cret"], await PluginBench.QueryAsync(scan.Registrations, "t x"));
        Assert.DoesNotContain("s3cret", File.ReadAllText(_bench.Paths.PluginSettings), StringComparison.Ordinal);

        var again = _bench.Scan(_bench.Manager());
        Assert.Equal(["alpha one x", "greeting howzit", "secret s3cret"], await PluginBench.QueryAsync(again.Registrations, "t x"));
    }

    [Fact]
    public async Task Unloading_frees_a_plugin_when_nothing_holds_it()
    {
        _bench.InstallAlpha();
        _bench.EnableAll();
        var manager = _bench.Manager();
        var scan = _bench.Scan(manager);
        manager.Adopt(scan);
        await LoadAndDropResultsAsync(scan);

        var report = await manager.UnloadAsync(manager.Adopt(new PluginScan([], [])), Budget);

        Assert.Equal(["tests.alpha"], report.Unloaded);
        Assert.Empty(report.Lingering);
    }

    [Fact]
    public async Task A_result_the_host_still_holds_does_not_keep_its_plugin_and_says_so_when_run()
    {
        _bench.InstallAlpha();
        _bench.EnableAll();
        var manager = _bench.Manager();
        var scan = _bench.Scan(manager);
        manager.Adopt(scan);

        // As a window that drew the row might, for longer than anyone should wait.
        var held = await FirstResultAsync(scan);
        var report = await manager.UnloadAsync(manager.Adopt(new PluginScan([], [])), Budget);

        Assert.Equal(["tests.alpha"], report.Unloaded);
        var refused = await Assert.ThrowsAsync<LaunchRefusedException>(async () => await held.Primary.Execute(new ActionContext(Query.Global("x")), CancellationToken.None));
        Assert.Contains("reloaded", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plugin_that_leaves_a_thread_running_is_reported_as_still_in_memory()
    {
        _bench.InstallAlpha(type: "Hail.TestPlugin.Alpha.ThreadLeavingProvider");
        _bench.EnableAll();
        var manager = _bench.Manager();
        var scan = _bench.Scan(manager);
        manager.Adopt(scan);
        await LoadAndDropResultsAsync(scan);

        var report = await manager.UnloadAsync(manager.Adopt(new PluginScan([], [])), Budget);

        Assert.Equal(["tests.alpha"], report.Lingering);
    }

    [Fact]
    public async Task The_everything_plugin_loads_says_when_everything_is_not_running_and_unloads()
    {
        _bench.Install("vrey.everything", PluginBench.BuiltEverything(), type: null, entry: "Hail.Plugin.Everything.dll");
        _bench.EnableAll();
        var manager = _bench.Manager();
        var scan = _bench.Scan(manager);
        manager.Adopt(scan);

        var entry = Assert.Single(scan.Entries);
        Assert.Equal("Everything", entry.Name);
        Assert.Equal(2, entry.Settings.Schema.Count);

        // This machine has no Everything; were one running, the rows would be its files.
        var titles = await PluginBench.QueryAsync(scan.Registrations, "e report");
        Assert.True(entry.Provider!.IsLoaded, entry.Provider.LoadProblem);
        Assert.Equal(["Everything is not running."], titles);
        Assert.Empty(await PluginBench.QueryAsync(scan.Registrations, "report"));

        // Its reply window's thread is stopped by Dispose, or the context could not go.
        var report = await manager.UnloadAsync(manager.Adopt(new PluginScan([], [])), Budget);
        Assert.Equal(["vrey.everything"], report.Unloaded);
    }

    private static async Task LoadAndDropResultsAsync(PluginScan scan) => Assert.NotEmpty(await PluginBench.QueryAsync(scan.Registrations, "t x"));

    private static async Task<Result> FirstResultAsync(PluginScan scan)
    {
        var provider = scan.Registrations[0];
        await provider.Provider.InitializeAsync(provider.Context, CancellationToken.None);
        await foreach (var result in provider.Provider.QueryAsync(Query.Global("x"), CancellationToken.None))
        {
            return result;
        }

        throw new InvalidOperationException("The plugin answered nothing.");
    }
}
