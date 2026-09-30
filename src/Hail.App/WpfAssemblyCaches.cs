using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;

namespace Hail.App;

/// <summary>
/// Clears plugins out of WPF's caches of loaded assemblies, which would otherwise keep every
/// unloaded plugin in memory until Hail quits (Hail.md §20).
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of cache, both private and both static for the process, keep an assembly weakly
/// only when it is dynamic and strongly otherwise. A plugin's assembly is collectible but not
/// dynamic, so once WPF has looked at it, its context can never unload:
/// </para>
/// <list type="bullet">
/// <item><c>SafeSecurityHelper._assemblies</c> in WindowsBase, PresentationCore and System.Xaml:
/// the name of every loaded assembly, filled whenever WPF looks one up by name (a pack URI, a
/// type in XAML). Found with a heap snapshot: about one reload in ten left a plugin behind,
/// rooted only through the WindowsBase one.</item>
/// <item>The shared XAML schema context's <c>_xmlnsInfo</c> and its relatives, filled when a
/// XAML namespace is resolved by scanning every loaded assembly. (Its list of assemblies not
/// yet scanned is a weak list, and needs nothing.)</item>
/// </list>
/// <para>
/// All of them are caches WPF refills on demand, and a plugin never supplies XAML, so dropping
/// the collectible entries costs nothing. If a WPF release renames the fields, this finds
/// nothing and does nothing, and an unload that then fails is reported as it always was.
/// </para>
/// </remarks>
internal static class WpfAssemblyCaches
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly (Assembly Owner, string Type)[] NameCaches =
    [
        (typeof(DependencyObject).Assembly, "MS.Internal.WindowsBase.SafeSecurityHelper"),
        (typeof(UIElement).Assembly, "MS.Internal.PresentationCore.SafeSecurityHelper"),
        (typeof(System.Xaml.XamlReader).Assembly, "System.Xaml.SafeSecurityHelper"),
    ];

    /// <summary>Removes every collectible assembly (and type of one) from WPF's caches; returns how many entries went.</summary>
    public static int ForgetCollectible() => ForgetInNameCaches() + ForgetInSchemaContext();

    private static int ForgetInNameCaches()
    {
        var forgotten = 0;
        foreach (var (owner, typeName) in NameCaches)
        {
            var type = owner.GetType(typeName, throwOnError: false);
            if (type?.GetField("_assemblies", Private)?.GetValue(null) is IDictionary cache
                && type.GetField("syncObject", Private)?.GetValue(null) is { } gate)
            {
                lock (gate)
                {
                    forgotten += RemoveCollectible(cache);
                }
            }
        }

        return forgotten;
    }

    private static int ForgetInSchemaContext()
    {
        var context = XamlReader.GetWpfSchemaContext();
        var forgotten = 0;
        foreach (var field in typeof(System.Xaml.XamlSchemaContext).GetFields(PrivateInstance))
        {
            // Concurrent dictionaries only: safe to change from any thread without WPF's locks.
            if (field.FieldType.Name.StartsWith("ConcurrentDictionary", StringComparison.Ordinal) && field.GetValue(context) is IDictionary dictionary)
            {
                forgotten += RemoveCollectible(dictionary);
            }
        }

        return forgotten;
    }

    private static int RemoveCollectible(IDictionary cache)
    {
        var keys = cache.Keys.Cast<object>().Where(IsCollectible).ToArray();
        foreach (var key in keys)
        {
            cache.Remove(key);
        }

        return keys.Length;
    }

    private static bool IsCollectible(object key) => key switch
    {
        Assembly assembly => assembly.IsCollectible,
        Type type => type.Assembly.IsCollectible,
        _ => false,
    };
}
