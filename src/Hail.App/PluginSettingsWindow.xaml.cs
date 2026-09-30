using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hail.Core.Plugins;
using Hail.Core.Ports;
using Hail.Plugins;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Hail.App;

/// <summary>
/// A form for one plugin's settings, one row per declaration in its manifest (Hail.md §6.2).
/// Saved values reach the plugin at its next read; a secret is encrypted before it leaves this
/// window and is never shown again, only replaced or removed.
/// </summary>
internal sealed partial class PluginSettingsWindow
{
    private readonly PluginEntry _entry;
    private readonly ISecretProtector _protector;
    private readonly Action<IReadOnlyDictionary<string, SettingValue>> _save;
    private readonly List<Func<SettingValue?>> _readers = [];

    /// <param name="save">Writes the values; throws <see cref="IOException"/> when the disk refuses.</param>
    public PluginSettingsWindow(PluginEntry entry, ISecretProtector protector, Action<IReadOnlyDictionary<string, SettingValue>> save)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(save);
        _entry = entry;
        _protector = protector;
        _save = save;

        InitializeComponent();

        Heading.Text = $"{entry.Name} settings";
        Subheading.Text = "Saved settings reach the plugin at once; no reload is needed.";

        var stored = entry.Settings.Values;
        foreach (var setting in entry.Settings.Schema)
        {
            Fields.Children.Add(Row(setting, stored.GetValueOrDefault(setting.Key) ?? setting.Default));
        }
    }

    public PluginEntry Entry => _entry;

    private StackPanel Row(SettingDefinition setting, SettingValue current)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        var label = new TextBlock
        {
            Text = setting.Label,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        row.Children.Add(label);

        if (setting.Description is { } description)
        {
            var line = new TextBlock { Text = description, FontSize = 13, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
            line.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            row.Children.Add(line);
        }

        FrameworkElement control = setting.Kind switch
        {
            SettingKind.Toggle => Toggle(setting, current),
            SettingKind.Choice => Choice(setting, current),
            SettingKind.Secret => Secret(setting, current),
            _ => Text(setting, current),
        };
        control.Margin = new Thickness(0, 8, 0, 0);
        row.Children.Add(control);
        return row;
    }

    private TextBox Text(SettingDefinition setting, SettingValue current)
    {
        var box = new TextBox
        {
            Text = current is SettingValue.Text text ? text.Value : setting.DefaultText,
            MaxLength = SettingDefinition.MaxTextLength,
        };
        AutomationName(box, setting);
        _readers.Add(() => new SettingValue.Text(box.Text));
        return box;
    }

    private ToggleSwitch Toggle(SettingDefinition setting, SettingValue current)
    {
        var toggle = new ToggleSwitch
        {
            IsChecked = current is SettingValue.Toggle on ? on.Value : setting.DefaultToggle,
            OnContent = "On",
            OffContent = "Off",
        };
        AutomationName(toggle, setting);
        _readers.Add(() => new SettingValue.Toggle(toggle.IsChecked == true));
        return toggle;
    }

    private ComboBox Choice(SettingDefinition setting, SettingValue current)
    {
        var box = new ComboBox { ItemsSource = setting.Choices, MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
        box.SelectedItem = current is SettingValue.Text chosen && setting.Choices.Contains(chosen.Value) ? chosen.Value : setting.DefaultText;
        AutomationName(box, setting);
        _readers.Add(() => new SettingValue.Text((string)box.SelectedItem));
        return box;
    }

    /// <summary>
    /// A secret already saved is never put back in the box: typing replaces it, Remove clears
    /// it, and leaving the box empty keeps it as it is.
    /// </summary>
    private StackPanel Secret(SettingDefinition setting, SettingValue current)
    {
        var saved = current as SettingValue.ProtectedSecret;
        var removed = false;

        var box = new PasswordBox
        {
            PlaceholderText = saved is null ? "Not set" : "Saved. Type to replace it.",
            MaxLength = SettingDefinition.MaxTextLength,
        };
        AutomationName(box, setting);

        var remove = new Button { Content = "Remove", Margin = new Thickness(8, 0, 0, 0), Visibility = saved is null ? Visibility.Collapsed : Visibility.Visible };
        remove.Click += (_, _) =>
        {
            removed = true;
            box.Password = string.Empty;
            box.PlaceholderText = "Removed when you save.";
            remove.Visibility = Visibility.Collapsed;
        };

        _readers.Add(() =>
        {
            if (box.Password.Length > 0)
            {
                return PluginSettings.ProtectSecret(_protector, _entry.Id, setting.Key, box.Password);
            }

            return removed || saved is null ? null : saved;
        });

        var line = new DockPanel();
        DockPanel.SetDock(remove, Dock.Right);
        line.Children.Add(remove);
        line.Children.Add(box);

        var hint = new TextBlock { Text = "Kept encrypted for your Windows account.", FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

        var panel = new StackPanel();
        panel.Children.Add(line);
        panel.Children.Add(hint);
        return panel;
    }

    private static void AutomationName(DependencyObject control, SettingDefinition setting) =>
        System.Windows.Automation.AutomationProperties.SetName(control, setting.Label);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var values = new Dictionary<string, SettingValue>(StringComparer.Ordinal);
        try
        {
            var schema = _entry.Settings.Schema;
            for (var i = 0; i < schema.Count; i++)
            {
                if (_readers[i]() is { } value)
                {
                    values[schema[i].Key] = value;
                }
            }

            _save(values);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Problem.Text = $"The settings could not be saved ({ex.GetType().Name}). The log has the reason.";
            Problem.Visibility = Visibility.Visible;
            return;
        }

        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
