using System.Security.Cryptography;
using System.Text;
using Hail.Core.Ports;
using Hail.Sdk;

namespace Hail.Core.Plugins;

/// <summary>
/// A plugin's settings as it reads them (<see cref="IPluginSettings"/>): the manifest's
/// declarations, the user's values where there are any, and the defaults everywhere else.
/// Thread-safe; the values are replaced whole when the user saves the form, and the plugin
/// sees the new ones at its next read.
/// </summary>
public sealed class PluginSettings : IPluginSettings
{
    private readonly string _pluginId;
    private readonly Dictionary<string, SettingDefinition> _schema;
    private readonly ISecretProtector? _protector;
    private readonly IHostLog? _log;
    private IReadOnlyDictionary<string, SettingValue> _values;

    public PluginSettings(
        string pluginId,
        IReadOnlyList<SettingDefinition> schema,
        IReadOnlyDictionary<string, SettingValue> values,
        ISecretProtector? protector,
        IHostLog? log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);
        _pluginId = pluginId;
        Schema = schema;
        _schema = schema.ToDictionary(s => s.Key, StringComparer.Ordinal);
        _protector = protector;
        _log = log;
        _values = Accepted(values);
    }

    /// <summary>For a provider that declares nothing: every read is a mistake, and says so.</summary>
    public static PluginSettings None(string pluginId) =>
        new(pluginId, [], new Dictionary<string, SettingValue>(), protector: null, log: null);

    public IReadOnlyList<SettingDefinition> Schema { get; }

    /// <summary>The values the user has set, by key; a key that is absent has its default.</summary>
    public IReadOnlyDictionary<string, SettingValue> Values => Volatile.Read(ref _values);

    /// <summary>
    /// What a secret is encrypted with, beside the account: the plugin and the key, so a value
    /// copied from one setting to another, or from one plugin to another, does not decrypt.
    /// </summary>
    public static byte[] PurposeFor(string pluginId, string key) =>
        Encoding.UTF8.GetBytes($"Hail plugin secret\n{pluginId}\n{key}");

    /// <summary>Encrypts <paramref name="plain"/> as the value of the secret <paramref name="key"/>.</summary>
    public static SettingValue ProtectSecret(ISecretProtector protector, string pluginId, string key, string plain)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(plain);
        if (plain.Length > SettingDefinition.MaxTextLength)
        {
            throw new ArgumentException($"A secret is at most {SettingDefinition.MaxTextLength} characters.", nameof(plain));
        }

        return new SettingValue.ProtectedSecret(protector.Protect(Encoding.UTF8.GetBytes(plain), PurposeFor(pluginId, key)));
    }

    /// <summary>The values the user just saved; anything that does not fit its declaration keeps its default.</summary>
    public void Replace(IReadOnlyDictionary<string, SettingValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Volatile.Write(ref _values, Accepted(values));
    }

    public string GetText(string key) => TextOf(key, SettingKind.Text);

    public string GetChoice(string key) => TextOf(key, SettingKind.Choice);

    public bool GetToggle(string key) =>
        ValueOf(key, SettingKind.Toggle) is SettingValue.Toggle toggle ? toggle.Value : Declared(key).DefaultToggle;

    public string? GetSecret(string key)
    {
        if (ValueOf(key, SettingKind.Secret) is not SettingValue.ProtectedSecret secret || _protector is null)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(_protector.Unprotect(secret.Protected, PurposeFor(_pluginId, key)));
        }
        catch (CryptographicException)
        {
            // Saved under another Windows account, or the file was copied from another machine.
            _log?.LogError($"The secret \"{key}\" of {_pluginId} could not be decrypted for this Windows account; it reads as not set until it is entered again.");
            return null;
        }
    }

    private string TextOf(string key, SettingKind kind) =>
        ValueOf(key, kind) is SettingValue.Text text ? text.Value : Declared(key).DefaultText;

    private SettingValue ValueOf(string key, SettingKind kind)
    {
        var declared = Declared(key);
        if (declared.Kind != kind)
        {
            throw new ArgumentException(
                $"The setting \"{key}\" of {_pluginId} is declared as {declared.Kind.ToString().ToLowerInvariant()}, not {kind.ToString().ToLowerInvariant()}.",
                nameof(key));
        }

        return Values.TryGetValue(key, out var value) ? value : declared.Default;
    }

    private SettingDefinition Declared(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _schema.TryGetValue(key, out var declared)
            ? declared
            : throw new ArgumentException($"{_pluginId} has no setting \"{key}\" in its plugin.json.", nameof(key));
    }

    private Dictionary<string, SettingValue> Accepted(IReadOnlyDictionary<string, SettingValue> values) =>
        values
            .Where(pair => _schema.TryGetValue(pair.Key, out var declared) && declared.Accepts(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}
