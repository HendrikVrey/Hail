using Hail.Sdk;

namespace Hail.Core.Hosting;

/// <summary>
/// A provider, the context it is given, and how the host drives it: which keywords scope a
/// query to it, whether it answers every query, and how long it waits for typing to settle.
/// </summary>
/// <param name="Id">Stable, and the key its results are remembered under.</param>
/// <param name="Name">What the box calls it when a keyword scopes a query to it.</param>
public sealed record ProviderRegistration(string Id, string Name, IProvider Provider, IPluginContext Context)
{
    /// <summary>
    /// Words that, typed first, send the query to this provider alone. A keyword made of
    /// letters needs a space after it (<c>g cats</c>), so typing a word that starts with one is
    /// not mistaken for it; a keyword made of symbols does not (<c>=2+2</c>).
    /// </summary>
    public IReadOnlyList<ProviderKeyword> Keywords { get; init; } = [];

    /// <summary>
    /// Whether the provider answers every query, or only one scoped to it by keyword. A slow or
    /// noisy provider stays out of every keystroke this way (Hail.md §6.2).
    /// </summary>
    public bool IsGlobal { get; init; } = true;

    /// <summary>
    /// How long typing must pause before the provider is asked. Zero for anything that answers
    /// from memory; a provider that does I/O declares one so a word is not asked for letter by
    /// letter (Hail.md §6.4).
    /// </summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Whether what this provider finds is remembered in history (it implements
    /// <see cref="IRecall"/>). For a plugin that is known only once it has loaded.
    /// </summary>
    public bool IsRemembered => Provider is IProviderProxy proxy ? proxy.Recalls : Provider is IRecall;
}

/// <summary>
/// A provider that stands in for another loaded later (a plugin), and so implements
/// <see cref="IRecall"/> whether or not the one it stands for does.
/// </summary>
public interface IProviderProxy
{
    /// <summary>Whether the provider stood in for implements <see cref="IRecall"/>; false until it is loaded.</summary>
    bool Recalls { get; }
}

/// <summary>A keyword and the name the box shows while it is in force (<c>g</c>, Google).</summary>
public sealed record ProviderKeyword(string Keyword, string Label)
{
    /// <summary>
    /// A keyword of symbols only (<c>=</c>) is in force as soon as it is typed; one with a
    /// letter or digit in it only once a space follows it.
    /// </summary>
    public bool NeedsSpace => Keyword.Any(char.IsLetterOrDigit);
}
