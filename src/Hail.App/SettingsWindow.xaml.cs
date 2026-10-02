using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Hail.Core.Plugins;
using Hail.Core.Ports;
using Hail.Core.Settings;
using Hail.Core.Updates;
using Hail.Plugins;
using Hail.Providers.Web;
using Hail.Windows.Startup;
using Hail.Windows.Windowing;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Hail.App;

/// <summary>
/// Hail's settings window (Hail.md §10.4). It holds no settings of its own: every change goes to
/// <see cref="ISettingsHost"/>, which saves it and puts it in force, and the window redraws from
/// what the host then says.
/// </summary>
internal sealed partial class SettingsWindow
{
    private readonly ISettingsHost _host;
    private readonly ObservableCollection<EngineRow> _engines = [];
    private bool _loading;
    private bool _recording;
    private bool _quitting;

    public SettingsWindow(ISettingsHost host, SettingsSection section)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;

        InitializeComponent();

        EngineRows.ItemsSource = _engines;
        DefaultEngine.ItemsSource = _engines;

        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(Hook);
        _host.PluginsChanged += OnPluginsChanged;
        _host.Updates.StatusChanged += OnUpdateStatusChanged;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _host.PluginsChanged -= OnPluginsChanged;
            _host.Updates.StatusChanged -= OnUpdateStatusChanged;
            CancelRecording();
        };

        // The recorder listens only while the user is looking at it. Anything that moves them
        // on (another window, another section, a click elsewhere) ends it and takes the old
        // shortcut back, or a Ctrl+A typed into a text box later would become the shortcut and
        // Alt+Space would stay dead meanwhile. It says so, or the shortcut looks to have reset
        // by itself (a Win+ chord Windows keeps for itself takes the focus away exactly so).
        Deactivated += (_, _) => CancelRecording(becauseFocusMoved: true);
        ChangeHotkeyButton.LostKeyboardFocus += (_, _) => CancelRecording(becauseFocusMoved: true);

        LoadAll();
        ShowSection(section);
    }

    /// <summary>The section showing now.</summary>
    public SettingsSection Section { get; private set; }

    /// <summary>Whether the shortcut recorder is waiting for keys.</summary>
    public bool IsRecordingHotkey => _recording;

    /// <summary>Closes the window because Hail is quitting: engines that pass are saved, others are let go.</summary>
    public void CloseForQuit()
    {
        _quitting = true;
        Close();
    }

    /// <summary>Brings <paramref name="section"/> into view.</summary>
    public void ShowSection(SettingsSection section)
    {
        Section = section;
        foreach (ListBoxItem item in Nav.Items)
        {
            if (Equals(item.Tag, section.ToString()))
            {
                Nav.SelectedItem = item;
            }
        }

        foreach (var (panel, which) in Panels())
        {
            panel.Visibility = which == section ? Visibility.Visible : Visibility.Collapsed;
        }

        Scroller.ScrollToTop();
    }

    private IEnumerable<(StackPanel Panel, SettingsSection Section)> Panels() =>
    [
        (GeneralSection, SettingsSection.General),
        (ProvidersSection, SettingsSection.Providers),
        (WebSearchSection, SettingsSection.WebSearch),
        (PluginsSection, SettingsSection.Plugins),
        (HistorySection, SettingsSection.History),
        (UpdatesSection, SettingsSection.Updates),
        (AboutSection, SettingsSection.About),
    ];

    private void LoadAll()
    {
        _loading = true;
        try
        {
            LoadGeneral();
            LoadProviders();
            LoadEngines();
            LoadPlugins();
            LoadHistory();
            LoadUpdates();
            AboutVersion.Text = $"Version {_host.Version}";
        }
        finally
        {
            _loading = false;
        }
    }

    // ---- General --------------------------------------------------------------------------

    private void LoadGeneral()
    {
        var settings = _host.Settings;
        if (!_recording)
        {
            HotkeyText.Text = settings.Hotkey.Display;
            ChangeHotkeyButton.Content = "Change";
            HotkeyHint.Text = _host.HotkeyRegistered
                ? settings.Hotkey == Chord.AltSpace
                    ? "Shows the box from anywhere. Alt+Space also opens a window's own menu in Windows; while Hail runs, it opens the box instead."
                    : "Shows the box from anywhere."
                : $"{settings.Hotkey.Display} is not registered, because another program holds it. Choose another, or click Hail's icon to open the box.";
        }

        var state = _host.StartupState;
        StartupToggle.IsChecked = state == StartupState.On;
        StartupHint.Text = state switch
        {
            StartupState.DisabledByUser => "Switched off in Task Manager's Startup apps. Switching it on here switches it back on.",
            StartupState.OnElsewhere => "Another copy of Hail is set to start. Switching it on here makes it this one.",
            _ => "Hail starts, out of sight, when you sign in, so the shortcut always works.",
        };

        KeepQueryToggle.IsChecked = settings.KeepLastQuery;
    }

    private void ChangeHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_recording)
        {
            EndRecording(resume: true);
            return;
        }

        _recording = true;
        _host.SuspendHotkey();
        HotkeyProblem.Visibility = Visibility.Collapsed;
        HotkeyNote.Visibility = Visibility.Collapsed;
        HotkeyText.Text = "Press keys";
        HotkeyHint.Text = "Press the new shortcut, holding Ctrl, Alt or Win with a letter, a digit, Space or a function key. Escape cancels.";
        ChangeHotkeyButton.Content = "Cancel";
        Keyboard.Focus(ChangeHotkeyButton);
    }

    /// <summary>Ends a recording that is under way, taking the saved shortcut back. Does nothing otherwise.</summary>
    /// <param name="becauseFocusMoved">The user did not ask; the window says why nothing changed.</param>
    internal void CancelRecording(bool becauseFocusMoved = false)
    {
        if (!_recording)
        {
            return;
        }

        EndRecording(resume: true);
        if (becauseFocusMoved)
        {
            HotkeyProblem.Visibility = Visibility.Collapsed;
            Report(HotkeyNote, $"Not changed: the keys stopped reaching this window before a shortcut was pressed, so {_host.Settings.Hotkey.Display} still shows the box. Choose Change and press the new shortcut here.");
        }
    }

    private void EndRecording(bool resume)
    {
        _recording = false;
        if (resume)
        {
            _host.ResumeHotkey();
        }

        LoadGeneral();
    }

    /// <summary>A key while recording: modifiers show as they are held; the first other key ends it.</summary>
    private void Record(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };

        var modifiers = CurrentModifiers();
        if (key == Key.Escape && modifiers == ChordModifiers.None)
        {
            EndRecording(resume: true);
            return;
        }

        if (IsModifier(key))
        {
            ShowHeldModifiers(modifiers);
            return;
        }

        var chord = new Chord(modifiers, KeyInterop.VirtualKeyFromKey(key));
        if (chord.Problem() is { } refused)
        {
            ShowHotkeyProblem($"{Capitalise(refused)}.");
            return;
        }

        if (_host.TryHotkey(chord) is { } problem)
        {
            ShowHotkeyProblem($"{problem} Press another shortcut, or Escape to keep {_host.Settings.Hotkey.Display}.");
            HotkeyText.Text = "Press keys";
            return;
        }

        HotkeyProblem.Visibility = Visibility.Collapsed;
        EndRecording(resume: false);
        Report(HotkeyNote, $"Saved. {chord.Display} shows the box now.");
    }

    /// <summary>A key let go while recording: the keycap follows the modifiers still held, back to "Press keys".</summary>
    private void RecordRelease(KeyEventArgs e)
    {
        e.Handled = true;
        ShowHeldModifiers(CurrentModifiers());
    }

    private void ShowHeldModifiers(ChordModifiers modifiers) =>
        HotkeyText.Text = modifiers == ChordModifiers.None ? "Press keys" : Chord.ModifiersDisplay(modifiers) + "+";

    private static ChordModifiers CurrentModifiers()
    {
        var modifiers = ChordModifiers.None;
        var held = Keyboard.Modifiers;
        if (held.HasFlag(ModifierKeys.Control))
        {
            modifiers |= ChordModifiers.Control;
        }

        if (held.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= ChordModifiers.Alt;
        }

        if (held.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= ChordModifiers.Shift;
        }

        if (held.HasFlag(ModifierKeys.Windows) || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
        {
            modifiers |= ChordModifiers.Windows;
        }

        return modifiers;
    }

    private static bool IsModifier(Key key) => key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private void ShowHotkeyProblem(string text)
    {
        HotkeyProblem.Text = text;
        HotkeyProblem.Visibility = Visibility.Visible;
    }

    private void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        var problem = _host.SetStartup(StartupToggle.IsChecked == true);
        Report(StartupProblem, problem);
        _loading = true;
        try
        {
            LoadGeneral();
        }
        finally
        {
            _loading = false;
        }
    }

    private void KeepQueryToggle_Click(object sender, RoutedEventArgs e) =>
        ApplyAndReport(_host.Settings with { KeepLastQuery = KeepQueryToggle.IsChecked == true }, GeneralProblem, LoadGeneral);

    // ---- Providers ------------------------------------------------------------------------

    private void LoadProviders()
    {
        ProviderRows.Children.Clear();
        foreach (var provider in _host.BuiltInProviders)
        {
            ProviderRows.Children.Add(ProviderCard(provider));
        }
    }

    private Border ProviderCard(BuiltInProvider provider)
    {
        var toggle = new ToggleSwitch
        {
            IsChecked = _host.Settings.IsEnabled(provider.Id),
            OnContent = "On",
            OffContent = "Off",
            Tag = provider.Id,
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle, provider.Name);
        toggle.Click += (_, _) => ApplyAndReport(
            _host.Settings.WithProvider(provider.Id, toggle.IsChecked == true),
            ProvidersProblem,
            LoadProviders);
        DockPanel.SetDock(toggle, Dock.Right);

        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(Styled(new TextBlock { Text = provider.Name }, "CardTitle"));
        text.Children.Add(Styled(new TextBlock { Text = provider.Description }, "CardText"));
        if (provider.Keywords.Count > 0)
        {
            var keywords = provider.Keywords.Count == 1
                ? $"Alone: start with {provider.Keywords[0]}"
                : $"Alone: start with {string.Join(", ", provider.Keywords.Take(provider.Keywords.Count - 1))} or {provider.Keywords[^1]}";
            text.Children.Add(Styled(new TextBlock { Text = keywords }, "CardText"));
        }

        var row = new DockPanel();
        row.Children.Add(toggle);
        row.Children.Add(text);
        return Card(row);
    }

    // ---- Web search -----------------------------------------------------------------------

    private void LoadEngines()
    {
        foreach (var row in _engines)
        {
            row.PropertyChanged -= OnEngineEdited;
        }

        _engines.Clear();
        var web = _host.Settings.WebSearch;
        foreach (var engine in web.Engines)
        {
            AddEngineRow(new EngineRow(engine.Name, engine.Keyword, engine.Template));
        }

        DefaultEngine.SelectedItem = _engines.FirstOrDefault(e => string.Equals(e.Keyword, web.DefaultKeyword, StringComparison.OrdinalIgnoreCase))
            ?? _engines.FirstOrDefault();
        SetEnginesDirty(false);
        EngineProblem.Visibility = Visibility.Collapsed;
    }

    private void AddEngineRow(EngineRow row)
    {
        row.PropertyChanged += OnEngineEdited;
        _engines.Add(row);
    }

    private void OnEngineEdited(object? sender, PropertyChangedEventArgs e) => SetEnginesDirty(true);

    private void SetEnginesDirty(bool dirty)
    {
        SaveEnginesButton.IsEnabled = dirty;
        UndoEnginesButton.IsEnabled = dirty;
        if (dirty)
        {
            EngineNote.Visibility = Visibility.Collapsed;
        }
    }

    private void AddEngine_Click(object sender, RoutedEventArgs e)
    {
        AddEngineRow(new EngineRow(string.Empty, string.Empty, "https://"));
        SetEnginesDirty(true);
    }

    private void RemoveEngine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EngineRow row })
        {
            var wasDefault = ReferenceEquals(DefaultEngine.SelectedItem, row);
            row.PropertyChanged -= OnEngineEdited;
            _engines.Remove(row);
            if (wasDefault)
            {
                DefaultEngine.SelectedItem = _engines.FirstOrDefault();
            }

            SetEnginesDirty(true);
        }
    }

    private void DefaultEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && e.AddedItems.Count > 0)
        {
            SetEnginesDirty(true);
        }
    }

    private void SaveEngines_Click(object sender, RoutedEventArgs e) => SaveEngines();

    /// <summary>Checks the engines as a whole and, if they pass, saves them; otherwise says the first problem.</summary>
    internal bool SaveEngines()
    {
        var engines = _engines.Select(r => new WebEngineSetting(r.Keyword.Trim(), r.Name.Trim(), r.Template.Trim())).ToList();
        var chosen = (DefaultEngine.SelectedItem as EngineRow)?.Keyword.Trim();

        if (WebEngineList.Check(engines, chosen, _host.KeywordsOutsideWebSearch) is { } problem)
        {
            Report(EngineProblem, problem);
            return false;
        }

        var next = _host.Settings with { WebSearch = new WebSearchOptions(engines, chosen ?? string.Empty) };
        if (_host.Apply(next) is { } failed)
        {
            Report(EngineProblem, failed);
            return false;
        }

        _loading = true;
        try
        {
            LoadEngines();
            LoadProviders();
        }
        finally
        {
            _loading = false;
        }

        EngineNote.Text = "Saved. The engines are in use now.";
        EngineNote.Visibility = Visibility.Visible;
        return true;
    }

    private void UndoEngines_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            LoadEngines();
        }
        finally
        {
            _loading = false;
        }
    }

    // ---- Plugins --------------------------------------------------------------------------

    private void OnPluginsChanged() => Dispatcher.BeginInvoke(() =>
    {
        LoadPlugins();
        LoadProviders();
    });

    private void LoadPlugins()
    {
        PluginRows.Children.Clear();
        var plugins = _host.Plugins;
        foreach (var entry in plugins)
        {
            PluginRows.Children.Add(PluginCard(entry));
        }

        NoPlugins.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border PluginCard(PluginEntry entry)
    {
        var manifest = entry.Plugin.Manifest;
        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(Styled(new TextBlock { Text = manifest is null ? entry.Name : $"{entry.Name}  {manifest.Version}" }, "CardTitle"));
        if (manifest is not null)
        {
            text.Children.Add(Styled(new TextBlock { Text = $"From {manifest.Publisher} ({entry.Id})" }, "CardText"));
        }

        text.Children.Add(Styled(new TextBlock { Text = _host.DescribePlugin(entry) }, "CardText"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (entry.Status is PluginStatus.New or PluginStatus.Changed or PluginStatus.Declined)
        {
            buttons.Children.Add(PluginButton("Enable...", _host.CanRecordPluginAnswers, () => _host.AskAboutPlugin(entry)));
        }

        if (entry.Status == PluginStatus.Enabled)
        {
            buttons.Children.Add(PluginButton("Switch off", _host.CanRecordPluginAnswers, () => _host.SwitchOffPlugin(entry)));
        }

        if (entry.Status != PluginStatus.Invalid && entry.Settings.Schema.Count > 0)
        {
            buttons.Children.Add(PluginButton("Settings...", true, () => _host.EditPluginSettings(entry)));
        }

        DockPanel.SetDock(buttons, Dock.Right);
        var row = new DockPanel();
        row.Children.Add(buttons);
        row.Children.Add(text);
        return Card(row);
    }

    private static Button PluginButton(string label, bool enabled, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(8, 0, 0, 0), IsEnabled = enabled };
        button.Click += (_, _) => action();
        return button;
    }

    private void OpenPluginsFolder_Click(object sender, RoutedEventArgs e) => _host.OpenPluginsFolder();

    private void ReloadPlugins_Click(object sender, RoutedEventArgs e) => _host.ReloadPlugins();

    // ---- History --------------------------------------------------------------------------

    private void LoadHistory()
    {
        var count = _host.HistoryCount;
        HistoryCountText.Text = count switch
        {
            0 => "Nothing remembered yet",
            1 => "1 pick remembered",
            _ => string.Create(CultureInfo.CurrentCulture, $"{count:N0} picks remembered"),
        };
        ClearHistoryButton.IsEnabled = count > 0;
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _host.ClearHistory();
        LoadHistory();
        HistoryCountText.Text = "History cleared";
    }

    // ---- Updates --------------------------------------------------------------------------

    private void OnUpdateStatusChanged() => LoadUpdates();

    private void LoadUpdates()
    {
        UpdateToggle.IsChecked = _host.Settings.CheckForUpdates == true;

        var running = _host.Updates.Running;
        UpdateProgress.Visibility = Visibility.Collapsed;
        switch (_host.Updates.Status)
        {
            case UpdateStatus.Checking:
                SetUpdateCard($"You have Hail {running}.", "Asking GitHub for the newest version...", null, null, null);
                break;

            case UpdateStatus.Offering offering:
                SetUpdateCard(
                    $"Hail {offering.Release.Version} is available. You have {running}.",
                    offering.Message ?? $"The installer is {offering.Release.DescribeSize()}. It is checked against GitHub's checksum before it runs, and Hail closes so it can be replaced.",
                    "Update",
                    "Later",
                    "Skip this version");
                break;

            case UpdateStatus.Downloading downloading:
                SetUpdateCard(
                    $"Downloading Hail {downloading.Release.Version}",
                    string.Create(CultureInfo.CurrentCulture, $"{downloading.Fraction:P0} of {downloading.Release.DescribeSize()}"),
                    null,
                    "Cancel",
                    null);
                UpdateProgress.Value = downloading.Fraction;
                UpdateProgress.Visibility = Visibility.Visible;
                break;

            case UpdateStatus.Installing installing:
                SetUpdateCard(
                    $"Installing Hail {installing.Release.Version}",
                    "The installer is open. Hail is closing so it can be replaced, and starts again when the installer finishes.",
                    null,
                    null,
                    null);
                break;

            case UpdateStatus.Idle idle:
                SetUpdateCard($"You have Hail {running}.", idle.Message ?? string.Empty, "Check now", null, null);
                break;
        }
    }

    private void SetUpdateCard(string headline, string message, string? primary, string? secondary, string? tertiary)
    {
        UpdateHeadline.Text = headline;
        UpdateMessage.Text = message;
        UpdateMessage.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Configure(UpdatePrimaryButton, primary);
        Configure(UpdateSecondaryButton, secondary);
        Configure(UpdateTertiaryButton, tertiary);

        static void Configure(Button button, string? label)
        {
            button.Content = label;
            button.Visibility = label is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void UpdateToggle_Click(object sender, RoutedEventArgs e) =>
        ApplyAndReport(_host.Settings with { CheckForUpdates = UpdateToggle.IsChecked == true }, UpdatesProblem, LoadUpdates);

    private void UpdatePrimary_Click(object sender, RoutedEventArgs e)
    {
        switch (_host.Updates.Status)
        {
            case UpdateStatus.Idle:
                _ = _host.Updates.CheckNowAsync();
                break;
            case UpdateStatus.Offering:
                _ = _host.InstallUpdateAsync();
                break;
        }
    }

    private void UpdateSecondary_Click(object sender, RoutedEventArgs e)
    {
        switch (_host.Updates.Status)
        {
            case UpdateStatus.Offering:
                _host.Updates.Later();
                break;
            case UpdateStatus.Downloading:
                _host.Updates.CancelDownload();
                break;
        }
    }

    private void UpdateTertiary_Click(object sender, RoutedEventArgs e) => _host.Updates.Skip();

    private void Releases_Click(object sender, RoutedEventArgs e) => _host.OpenReleasesPage();

    private void Project_Click(object sender, RoutedEventArgs e) => _host.OpenProjectPage();

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => _host.OpenDataFolder();

    // ---- Shared ---------------------------------------------------------------------------

    private void ApplyAndReport(HailSettings next, TextBlock problemLine, Action reload)
    {
        if (_loading)
        {
            return;
        }

        Report(problemLine, _host.Apply(next));

        _loading = true;
        try
        {
            reload();
        }
        finally
        {
            _loading = false;
        }
    }

    private static void Report(TextBlock line, string? problem)
    {
        line.Text = problem ?? string.Empty;
        line.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private T Styled<T>(T element, string style)
        where T : FrameworkElement
    {
        element.Style = (Style)FindResource(style);
        return element;
    }

    private Border Card(UIElement child) => new() { Style = (Style)FindResource("Card"), Child = child };

    private static string Capitalise(string sentence) =>
        sentence.Length == 0 ? sentence : char.ToUpperInvariant(sentence[0]) + sentence[1..];

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is ListBoxItem { Tag: string tag } && Enum.TryParse<SettingsSection>(tag, out var section) && section != Section)
        {
            CancelRecording();

            // Every other setting is saved as it changes; engines that pass their checks are
            // saved on the way out of their section too, so a change is not left behind there.
            if (Section == SettingsSection.WebSearch && SaveEnginesButton.IsEnabled)
            {
                SaveEngines();
            }

            ShowSection(section);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording)
        {
            Record(e);
            return;
        }

        if (e.Key != Key.Escape || DefaultEngine.IsDropDownOpen)
        {
            return;
        }

        e.Handled = true;
        Close();
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_recording)
        {
            RecordRelease(e);
        }
    }

    /// <summary>
    /// Unsaved engines are saved when the window closes, as every other setting already is. If
    /// they do not pass their checks the window stays open on them and says why, rather than
    /// dropping the change; only quitting Hail lets them go.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!SaveEnginesButton.IsEnabled || SaveEngines() || _quitting)
        {
            return;
        }

        e.Cancel = true;
        ShowSection(SettingsSection.WebSearch);
        EngineProblem.Text += " Fix it, or choose Undo changes to close without them.";
    }

    /// <summary>While recording, Alt+Space must reach the recorder, not open the window's own menu.</summary>
    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_recording && SystemMessages.IsKeyMenu(message, wParam))
        {
            handled = true;
        }

        return 0;
    }

    /// <summary>One web engine as its row in the editor holds it.</summary>
    internal sealed class EngineRow(string name, string keyword, string template) : INotifyPropertyChanged
    {
        private string _name = name;
        private string _keyword = keyword;
        private string _template = template;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name
        {
            get => _name;
            set => Set(ref _name, value);
        }

        public string Keyword
        {
            get => _keyword;
            set => Set(ref _keyword, value);
        }

        public string Template
        {
            get => _template;
            set => Set(ref _template, value);
        }

        private void Set(ref string field, string value, [CallerMemberName] string property = "")
        {
            if (field == value)
            {
                return;
            }

            field = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
