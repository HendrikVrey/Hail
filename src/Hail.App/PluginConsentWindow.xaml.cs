using System.Windows;
using Hail.Core.Plugins;
using Hail.Plugins;

namespace Hail.App;

/// <summary>
/// Asks whether a plugin may run (Hail.md §9), naming it, who it says made it, what it
/// answers and where it lives. Nothing is recorded unless a button is pressed: closing the
/// window leaves the question for later.
/// </summary>
internal sealed partial class PluginConsentWindow
{
    /// <summary>Characters of the fingerprint shown: enough to compare with what a publisher prints.</summary>
    private const int FingerprintShown = 16;

    public PluginConsentWindow(PluginEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var manifest = entry.Plugin.Manifest
            ?? throw new ArgumentException("A plugin that cannot be read cannot be asked about.", nameof(entry));
        Entry = entry;

        InitializeComponent();

        Heading.Text = entry.Status == PluginStatus.Changed
            ? "A plugin you enabled has changed. Enable the new files?"
            : "A plugin is waiting. Enable it?";
        PluginName.Text = manifest.Name;
        PluginVersion.Text = manifest.Version;
        Publisher.Text = $"Says it is from {manifest.Publisher}. Hail cannot check that.";
        Description.Text = manifest.Description ?? string.Empty;
        Description.Visibility = manifest.Description is null ? Visibility.Collapsed : Visibility.Visible;
        Answers.Text = DescribeAnswers(manifest);
        Folder.Text = entry.Plugin.Folder;
        Fingerprint.Text = entry.Plugin.Files!.Fingerprint[..FingerprintShown];
    }

    public PluginEntry Entry { get; }

    /// <summary>True to enable, false to keep it off, null when the window was closed without an answer.</summary>
    public bool? Answer { get; private set; }

    /// <summary>What the plugin answers, in a line: its keywords and whether it joins every search.</summary>
    public static string DescribeAnswers(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var keywords = string.Join(" or ", manifest.Keywords.Select(k => $"\"{k}\""));
        return (manifest.IsGlobal, manifest.Keywords.Count) switch
        {
            (true, 0) => "Every search",
            (true, _) => $"Every search, and alone when one starts with {keywords}",
            (false, 0) => "Nothing: it declares no keyword and does not join every search",
            (false, _) => $"Only searches that start with {keywords}",
        };
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void Enable_Click(object sender, RoutedEventArgs e)
    {
        Answer = true;
        Close();
    }

    private void KeepOff_Click(object sender, RoutedEventArgs e)
    {
        Answer = false;
        Close();
    }
}
