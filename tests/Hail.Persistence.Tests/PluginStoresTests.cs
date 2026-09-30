using Hail.Core.Plugins;

namespace Hail.Persistence.Tests;

/// <summary>
/// What the user answered for each plugin, and each plugin's settings, on disk: a damaged file
/// never enables anything, and a secret is never written as text.
/// </summary>
public sealed class PluginStoresTests : IDisposable
{
    private const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly SettingDefinition[] Schema =
    [
        new("greeting", SettingKind.Text, "Greeting", null, "hello", false, []),
        new("loud", SettingKind.Toggle, "Loud", null, string.Empty, false, []),
        new("size", SettingKind.Choice, "Size", null, "m", false, ["s", "m"]),
        new("token", SettingKind.Secret, "Token", null, string.Empty, false, []),
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "HailTests", Guid.NewGuid().ToString("N"));
    private readonly HailPaths _paths;

    public PluginStoresTests() => _paths = new HailPaths(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void An_answer_survives_a_restart()
    {
        var store = new PluginApprovalStore(_paths);
        Assert.Null(store.Load());
        store.Record("tests.alpha", new PluginApproval(true, Fingerprint));
        store.Record("tests.beta", new PluginApproval(false, Fingerprint));

        var loaded = new PluginApprovalStore(_paths);
        Assert.Null(loaded.Load());

        Assert.Equal(new PluginApproval(true, Fingerprint), loaded.For("tests.alpha"));
        Assert.Equal(new PluginApproval(true, Fingerprint), loaded.For("TESTS.ALPHA"));
        Assert.Equal(new PluginApproval(false, Fingerprint), loaded.For("tests.beta"));
        Assert.Null(loaded.For("tests.gamma"));
    }

    [Fact]
    public void A_damaged_answers_file_is_set_aside_and_enables_nothing()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_paths.PluginApprovals, "{ not json");

        var store = new PluginApprovalStore(_paths);
        var problem = store.Load();

        Assert.Contains("set aside", problem, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "plugins.unreadable.json")));
        Assert.Null(store.For("tests.alpha"));
    }

    [Fact]
    public void An_entry_with_a_fingerprint_that_is_not_one_is_ignored()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_paths.PluginApprovals, """{ "version": 1, "plugins": { "tests.alpha": { "enabled": true, "fingerprint": "*" } } }""");

        var store = new PluginApprovalStore(_paths);
        Assert.Null(store.Load());

        Assert.Null(store.For("tests.alpha"));
    }

    [Fact]
    public void A_newer_hails_answers_are_neither_read_nor_written_over()
    {
        Directory.CreateDirectory(_root);
        const string newer = """{ "version": 2, "plugins": {} }""";
        File.WriteAllText(_paths.PluginApprovals, newer);

        var store = new PluginApprovalStore(_paths);
        Assert.Contains("newer Hail", store.Load(), StringComparison.Ordinal);

        Assert.False(store.CanRecord);
        Assert.Throws<IOException>(() => store.Record("tests.alpha", new PluginApproval(true, Fingerprint)));
        Assert.Equal(newer, File.ReadAllText(_paths.PluginApprovals));
    }

    [Fact]
    public void A_version_that_is_not_a_number_does_not_stop_hail_starting()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_paths.PluginApprovals, $$"""{ "version": "1", "plugins": { "tests.alpha": { "enabled": true, "fingerprint": "{{Fingerprint}}" } } }""");

        var store = new PluginApprovalStore(_paths);

        Assert.Null(store.Load());
        Assert.True(store.CanRecord);
        Assert.Equal(new PluginApproval(true, Fingerprint), store.For("tests.alpha"));
    }

    [Fact]
    public void Settings_survive_a_restart_and_a_secret_is_stored_as_bytes_only()
    {
        var store = new PluginSettingsStore(_paths);
        Assert.Null(store.Load());
        var secret = new byte[] { 1, 2, 3, 250 };
        store.Save("tests.alpha", new Dictionary<string, SettingValue>
        {
            ["greeting"] = new SettingValue.Text("howzit"),
            ["loud"] = new SettingValue.Toggle(true),
            ["size"] = new SettingValue.Text("s"),
            ["token"] = new SettingValue.ProtectedSecret(secret),
        });

        var loaded = new PluginSettingsStore(_paths);
        Assert.Null(loaded.Load());
        var values = loaded.ValuesFor("tests.alpha", Schema);

        Assert.Equal(new SettingValue.Text("howzit"), values["greeting"]);
        Assert.Equal(new SettingValue.Toggle(true), values["loud"]);
        Assert.Equal(new SettingValue.Text("s"), values["size"]);
        Assert.Equal(secret, Assert.IsType<SettingValue.ProtectedSecret>(values["token"]).Protected);
        Assert.Contains("\"protected\"", File.ReadAllText(_paths.PluginSettings), StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_no_longer_fits_its_declaration_falls_back_alone()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_paths.PluginSettings, """{ "tests.alpha": { "greeting": true, "loud": false, "size": "xl", "token": "plain text" } }""");

        var store = new PluginSettingsStore(_paths);
        Assert.Null(store.Load());
        var values = store.ValuesFor("tests.alpha", Schema);

        Assert.Equal(["loud"], values.Keys);
    }

    [Fact]
    public void Saving_one_plugins_settings_keeps_the_others()
    {
        var store = new PluginSettingsStore(_paths);
        store.Load();
        store.Save("tests.alpha", new Dictionary<string, SettingValue> { ["loud"] = new SettingValue.Toggle(true) });
        store.Save("tests.beta", new Dictionary<string, SettingValue> { ["loud"] = new SettingValue.Toggle(false) });

        var loaded = new PluginSettingsStore(_paths);
        loaded.Load();

        Assert.Equal(new SettingValue.Toggle(true), loaded.ValuesFor("tests.alpha", Schema)["loud"]);
        Assert.Equal(new SettingValue.Toggle(false), loaded.ValuesFor("tests.beta", Schema)["loud"]);
    }

    [Fact]
    public void An_unreadable_settings_file_is_neither_used_nor_written_over()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_paths.PluginSettings, "{ broken");

        var store = new PluginSettingsStore(_paths);
        Assert.NotNull(store.Load());

        Assert.Empty(store.ValuesFor("tests.alpha", Schema));
        Assert.Throws<IOException>(() => store.Save("tests.alpha", new Dictionary<string, SettingValue>()));
        Assert.Equal("{ broken", File.ReadAllText(_paths.PluginSettings));
    }

    [Fact]
    public void A_data_folder_is_the_providers_own_and_never_somewhere_else()
    {
        Assert.Equal(Path.Combine(_root, "data", "tests.alpha"), _paths.DataFolderFor("tests.alpha"));
        Assert.Throws<ArgumentException>(() => _paths.DataFolderFor(@"..\..\Windows"));
        Assert.Throws<ArgumentException>(() => _paths.DataFolderFor("a/b"));
    }
}
