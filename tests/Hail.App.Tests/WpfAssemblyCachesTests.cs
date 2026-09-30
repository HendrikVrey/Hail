using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Windows.Markup;
using Hail.Sdk;

namespace Hail.App.Tests;

/// <summary>
/// WPF's own assembly-name cache keeps a collectible assembly for good once WPF has looked any
/// assembly up while it was loaded, so a plugin could never unload (Hail.md §20). Clearing the
/// cache is what lets it go.
/// </summary>
public sealed class WpfAssemblyCachesTests
{
    [Fact]
    public void An_unloaded_assembly_wpf_has_seen_goes_only_once_its_cache_forgets_it()
    {
        Ui.Run(() =>
        {
            var context = LoadAndLetWpfSee();

            Collect();
            Assert.True(context.IsAlive, "WPF's cache no longer holds collectible assemblies; WpfAssemblyCaches may be unnecessary now.");

            Assert.True(WpfAssemblyCaches.ForgetCollectible() > 0);
            Collect();
            Assert.False(context.IsAlive);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// A collectible context holding a copy of Hail.Sdk, loaded while XAML naming another assembly
    /// is parsed (which is what makes WPF read every loaded assembly's name), then unloaded.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndLetWpfSee()
    {
        var context = new AssemblyLoadContext("wpf cache test", isCollectible: true);
        using (var bytes = new MemoryStream(File.ReadAllBytes(typeof(IProvider).Assembly.Location)))
        {
            context.LoadFromStream(bytes);
        }

        XamlReader.Parse(
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
            + "xmlns:w=\"clr-namespace:System.Windows;assembly=WindowsBase\">"
            + "<w:Point x:Key=\"k\">1,2</w:Point></ResourceDictionary>");

        context.Unload();
        return new WeakReference(context);
    }

    private static void Collect()
    {
        for (var i = 0; i < 5; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}
