using System.Security.Cryptography;
using Hail.Core.Hosting;
using Hail.Core.History;
using Hail.Core.Plugins;
using Hail.Core.Ports;

namespace Hail.Core.Tests;

/// <summary>A plugin's settings as it reads them, its answers pinned to its files, and its part of history.</summary>
public sealed class PluginSettingsTests
{
    private static readonly SettingDefinition[] Schema =
    [
        new("greeting", SettingKind.Text, "Greeting", null, "hello", false, []),
        new("loud", SettingKind.Toggle, "Loud", null, string.Empty, true, []),
        new("size", SettingKind.Choice, "Size", null, "m", false, ["s", "m", "l"]),
        new("token", SettingKind.Secret, "Token", null, string.Empty, false, []),
    ];

    [Fact]
    public void Nothing_set_reads_as_the_manifests_defaults()
    {
        var settings = Settings(new Dictionary<string, SettingValue>());

        Assert.Equal("hello", settings.GetText("greeting"));
        Assert.True(settings.GetToggle("loud"));
        Assert.Equal("m", settings.GetChoice("size"));
        Assert.Null(settings.GetSecret("token"));
    }

    [Fact]
    public void Values_that_do_not_fit_their_declaration_keep_the_default_alone()
    {
        var settings = Settings(new Dictionary<string, SettingValue>
        {
            ["greeting"] = new SettingValue.Toggle(false),
            ["loud"] = new SettingValue.Toggle(false),
            ["size"] = new SettingValue.Text("xxl"),
            ["unknown"] = new SettingValue.Text("x"),
        });

        Assert.Equal("hello", settings.GetText("greeting"));
        Assert.False(settings.GetToggle("loud"));
        Assert.Equal("m", settings.GetChoice("size"));
    }

    [Fact]
    public void Asking_for_an_undeclared_key_or_with_the_wrong_kind_is_the_plugins_mistake_and_says_so()
    {
        var settings = Settings(new Dictionary<string, SettingValue>());

        Assert.Contains("has no setting \"colour\"", Assert.Throws<ArgumentException>(() => settings.GetText("colour")).Message, StringComparison.Ordinal);
        Assert.Contains("declared as toggle, not text", Assert.Throws<ArgumentException>(() => settings.GetText("loud")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_decrypts_only_for_the_plugin_and_key_it_was_saved_under()
    {
        var protector = new PurposeBoundProtector();
        var log = new RecordingLog();
        var saved = PluginSettings.ProtectSecret(protector, "tests.alpha", "token", "s3cret");

        Assert.Equal("s3cret", Settings(new Dictionary<string, SettingValue> { ["token"] = saved }, protector, log).GetSecret("token"));

        // The same bytes under another plugin's id read as not set, and the log says why without the value.
        var stolen = new PluginSettings("tests.beta", Schema, new Dictionary<string, SettingValue> { ["token"] = saved }, protector, log);
        Assert.Null(stolen.GetSecret("token"));
        Assert.Contains(log.Errors(), l => l.Contains("could not be decrypted", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains("s3cret", StringComparison.Ordinal));
    }

    [Fact]
    public void Replaced_values_are_what_the_next_read_sees()
    {
        var settings = Settings(new Dictionary<string, SettingValue>());

        settings.Replace(new Dictionary<string, SettingValue> { ["greeting"] = new SettingValue.Text("howzit") });

        Assert.Equal("howzit", settings.GetText("greeting"));
    }

    [Fact]
    public void A_provider_that_declares_nothing_has_nothing_to_read()
    {
        Assert.Throws<ArgumentException>(() => PluginSettings.None("hail.apps").GetToggle("x"));
    }

    [Theory]
    [InlineData(null, "abc", PluginStatus.New)]
    [InlineData(true, "abc", PluginStatus.Enabled)]
    [InlineData(true, "ABC", PluginStatus.Enabled)]
    [InlineData(true, "def", PluginStatus.Changed)]
    [InlineData(false, "abc", PluginStatus.Declined)]
    [InlineData(false, "def", PluginStatus.New)]
    public void An_answer_counts_only_for_the_files_it_was_given_for(bool? enabled, string fingerprintNow, PluginStatus status)
    {
        var approval = enabled is null ? null : new PluginApproval(enabled.Value, "abc");

        Assert.Equal(status, PluginApproval.StatusOf(approval, fingerprintNow));
    }

    [Fact]
    public void A_provider_reads_only_its_own_part_of_history()
    {
        var now = DateTimeOffset.UtcNow;
        var history = new UsageHistory();
        history.Record("a", new UsageKey("tests.alpha", "one"), now);
        history.Record("a", new UsageKey("tests.alpha", "two"), now);
        history.Record("a", new UsageKey("tests.alpha", "two"), now);
        history.Record("a", new UsageKey("hail.apps", "notepad"), now);

        Assert.Equal(["two", "one"], new ProviderHistory(history, "tests.alpha").MostPicked(8));
        Assert.Equal(["two"], new ProviderHistory(history, "tests.alpha").MostPicked(1));
        Assert.Empty(new ProviderHistory(history, "tests.alpha").MostPicked(0));
        Assert.Empty(ProviderHistory.Empty.MostPicked(8));
    }

    [Fact]
    public void A_plugins_logged_exception_keeps_its_type_and_loses_its_message()
    {
        var log = new RecordingLog();

        new PluginLog("tests.alpha", log).LogError("It broke", new InvalidOperationException("typed text: my password"));

        var line = Assert.Single(log.Lines);
        Assert.Contains("[tests.alpha] It broke", line, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", line, StringComparison.Ordinal);
        Assert.DoesNotContain("my password", line, StringComparison.Ordinal);
    }

    private static PluginSettings Settings(Dictionary<string, SettingValue> values, ISecretProtector? protector = null, IHostLog? log = null) =>
        new("tests.alpha", Schema, values, protector ?? new PurposeBoundProtector(), log);

    private sealed class PurposeBoundProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain, byte[] purpose) => [.. SHA256.HashData(purpose), .. plain];

        public byte[] Unprotect(byte[] protectedData, byte[] purpose) =>
            protectedData.AsSpan(0, 32).SequenceEqual(SHA256.HashData(purpose)) ? protectedData[32..] : throw new CryptographicException("Another purpose.");
    }
}
