namespace Hail.Core.Ports;

/// <summary>
/// An app the Start menu lists.
/// </summary>
/// <param name="Id">
/// The shell's parsing name for it inside the AppsFolder: an AppUserModelID such as
/// <c>Microsoft.WindowsCalculator_8wekyb3d8bbwe!App</c> for a packaged app, or a known-folder
/// path or an explicit AppUserModelID for a desktop one. Stable across refreshes, which is
/// what makes it a result id.
/// </param>
/// <param name="Name">What the Start menu calls it.</param>
public sealed record AppEntry(string Id, string Name)
{
    /// <summary>The parsing name that reaches this app through the shell.</summary>
    public string ShellPath => AppsFolder.PathFor(Id);
}

/// <summary>The apps the Start menu lists, as the host last read them.</summary>
/// <remarks>
/// A port: the apps provider depends on this and on nothing of the host's, and
/// <c>Hail.Windows</c> supplies the implementation. Reading <see cref="Apps"/> is free; how
/// and when the list is refreshed is the host's business.
/// </remarks>
public interface IAppCatalog
{
    IReadOnlyList<AppEntry> Apps { get; }
}

/// <summary>The one place the AppsFolder's shell path is spelt.</summary>
public static class AppsFolder
{
    public const string Prefix = @"shell:AppsFolder\";

    public static string PathFor(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Prefix + id;
    }
}
