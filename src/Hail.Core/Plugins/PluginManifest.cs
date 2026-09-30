using System.Globalization;

namespace Hail.Core.Plugins;

/// <summary>
/// What a plugin's <c>plugin.json</c> says about it (Hail.md §6.2), read before any of its code
/// is loaded: who it is, where its provider is, how the host drives it, and what it lets the
/// user set.
/// </summary>
/// <param name="Id">Lower case, dotted (<c>vrey.everything</c>), and the name of its folder.</param>
/// <param name="Name">What the box and the tray call it.</param>
/// <param name="Publisher">Who the plugin says made it. Nothing checks this; the consent says so.</param>
/// <param name="Version">The plugin's own version, shown and never compared.</param>
/// <param name="Sdk">The <c>Hail.Sdk</c> version it was built against.</param>
/// <param name="Entry">The assembly holding the provider, a file name in the plugin's folder.</param>
/// <param name="TypeName">The provider's full type name in that assembly.</param>
/// <param name="Keywords">Words that, typed first, send a query to it alone.</param>
/// <param name="IsGlobal">Whether it answers every query, or only one its keyword scopes.</param>
/// <param name="Debounce">How long typing must pause before it is asked.</param>
/// <param name="Description">One or two sentences for the consent and the tray, or null.</param>
/// <param name="Settings">What the user can set, drawn as a form by the host.</param>
public sealed record PluginManifest(
    string Id,
    string Name,
    string Publisher,
    string Version,
    SdkVersion Sdk,
    string Entry,
    string TypeName,
    IReadOnlyList<string> Keywords,
    bool IsGlobal,
    TimeSpan Debounce,
    string? Description,
    IReadOnlyList<SettingDefinition> Settings);

/// <summary>A <c>Hail.Sdk</c> version as a manifest names it: <c>1.0</c>.</summary>
public readonly record struct SdkVersion(int Major, int Minor)
{
    public static SdkVersion Of(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new SdkVersion(version.Major, version.Minor);
    }

    public static bool TryParse(string? text, out SdkVersion version)
    {
        version = default;
        var parts = text?.Split('.');
        if (parts is not { Length: 2 }
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || major > 999
            || minor > 999)
        {
            return false;
        }

        version = new SdkVersion(major, minor);
        return true;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");
}

/// <summary>
/// Whether a plugin built against one SDK can run in a host that speaks another (Hail.md §6.2).
/// </summary>
/// <remarks>
/// A minor version only adds, so the host runs anything of its own major up to its own minor. A
/// plugin built against a newer minor may call what this host does not have, and would fail
/// half way through a query with a missing method; it is refused whole, with a sentence.
/// </remarks>
public static class SdkCompatibility
{
    /// <summary>Null when <paramref name="plugin"/> runs on <paramref name="host"/>; otherwise why not.</summary>
    public static string? Problem(SdkVersion plugin, SdkVersion host)
    {
        if (plugin.Major > host.Major || (plugin.Major == host.Major && plugin.Minor > host.Minor))
        {
            return $"It is built for a newer Hail (SDK {plugin}); this Hail speaks SDK {host}. Update Hail to use it.";
        }

        return plugin.Major < host.Major
            ? $"It is built for SDK {plugin}, which this Hail (SDK {host}) no longer speaks. The plugin needs an update."
            : null;
    }
}

/// <summary>The kinds of setting a plugin can declare (Hail.md §6.2).</summary>
public enum SettingKind
{
    Text,
    Toggle,
    Choice,

    /// <summary>A token or a password: kept encrypted for the Windows account, never shown again.</summary>
    Secret,
}

/// <summary>One setting a plugin declares, from which the host draws its row in a form.</summary>
/// <param name="Key">What the plugin asks for it by.</param>
/// <param name="Label">What the form calls it.</param>
/// <param name="Description">A line under the label, or null.</param>
/// <param name="DefaultText">A text or choice setting's default; empty for the others.</param>
/// <param name="DefaultToggle">A toggle's default.</param>
/// <param name="Choices">A choice setting's values, in order; empty for the others.</param>
public sealed record SettingDefinition(
    string Key,
    SettingKind Kind,
    string Label,
    string? Description,
    string DefaultText,
    bool DefaultToggle,
    IReadOnlyList<string> Choices)
{
    /// <summary>The longest text or secret a setting holds; far beyond a token or an address.</summary>
    public const int MaxTextLength = 4096;

    /// <summary>The value the setting has until the user sets one.</summary>
    public SettingValue Default => Kind switch
    {
        SettingKind.Toggle => new SettingValue.Toggle(DefaultToggle),
        SettingKind.Secret => SettingValue.NotSet,
        _ => new SettingValue.Text(DefaultText),
    };

    /// <summary>Whether <paramref name="value"/> can be this setting's: the right kind, and for a choice one of its values.</summary>
    public bool Accepts(SettingValue value) => (Kind, value) switch
    {
        (SettingKind.Text, SettingValue.Text text) => text.Value.Length <= MaxTextLength,
        (SettingKind.Choice, SettingValue.Text text) => Choices.Contains(text.Value, StringComparer.Ordinal),
        (SettingKind.Toggle, SettingValue.Toggle) => true,
        (SettingKind.Secret, SettingValue.ProtectedSecret or SettingValue.NotSetValue) => true,
        _ => false,
    };
}

/// <summary>A setting's value as the host keeps it.</summary>
public abstract record SettingValue
{
    private SettingValue()
    {
    }

    /// <summary>A secret the user has not set, or has removed.</summary>
    public static SettingValue NotSet { get; } = new NotSetValue();

    public sealed record Text(string Value) : SettingValue;

    public sealed record Toggle(bool Value) : SettingValue;

    /// <summary>A secret, as encrypted for the Windows account; the plain text is never kept.</summary>
    public sealed record ProtectedSecret(byte[] Protected) : SettingValue;

    public sealed record NotSetValue : SettingValue;
}
