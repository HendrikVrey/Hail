using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Hail.Core.Ports;
using Hail.Core.Settings;
using Hail.Core.Updates;
using Hail.Plugins;
using Hail.Windows.Startup;

namespace Hail.App.Tests;

/// <summary>
/// The settings window built for real, off screen, against a fake host: what each control asks
/// the host for, and what the window says back.
/// </summary>
public sealed class SettingsWindowTests
{
    [Fact]
    public void It_opens_on_the_section_asked_for()
    {
        WindowUi.Run(host =>
        {
            using var shown = Shown(new SettingsWindow(host, SettingsSection.Updates));
            var window = shown.Window;

            Assert.Equal(Visibility.Visible, window.UpdatesSection.Visibility);
            Assert.Equal(Visibility.Collapsed, window.GeneralSection.Visibility);

            window.ShowSection(SettingsSection.Providers);
            Assert.Equal(Visibility.Visible, window.ProvidersSection.Visibility);
            Assert.Equal(Visibility.Collapsed, window.UpdatesSection.Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void A_provider_switched_off_is_saved_through_the_host()
    {
        WindowUi.Run(host =>
        {
            using var shown = Shown(new SettingsWindow(host, SettingsSection.Providers));
            var files = shown.Window.ProviderRows.Children.OfType<Border>()
                .Select(card => Find<Wpf.Ui.Controls.ToggleSwitch>(card))
                .Single(toggle => Equals(toggle.Tag, "hail.files"));

            Assert.True(files.IsChecked);
            files.IsChecked = false;
            files.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.False(host.Settings.IsEnabled("hail.files"));
            Assert.Equal(1, host.Applied);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Engines_that_fail_their_checks_are_not_saved_and_the_problem_is_shown()
    {
        WindowUi.Run(host =>
        {
            using var shown = Shown(new SettingsWindow(host, SettingsSection.WebSearch));
            var window = shown.Window;
            var rows = (IList<SettingsWindow.EngineRow>)window.EngineRows.ItemsSource;

            rows[1].Keyword = "g";
            Assert.True(window.SaveEnginesButton.IsEnabled);

            Assert.False(window.SaveEngines());
            Assert.Equal(Visibility.Visible, window.EngineProblem.Visibility);
            Assert.Contains("Two engines use the keyword", window.EngineProblem.Text, StringComparison.Ordinal);
            Assert.Equal(0, host.Applied);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void A_keyword_a_plugin_answers_to_cannot_be_taken_by_an_engine()
    {
        WindowUi.Run(host =>
        {
            host.Taken["e"] = "Everything";
            using var shown = Shown(new SettingsWindow(host, SettingsSection.WebSearch));
            var rows = (IList<SettingsWindow.EngineRow>)shown.Window.EngineRows.ItemsSource;

            rows[0].Keyword = "e";

            Assert.False(shown.Window.SaveEngines());
            Assert.Equal("\"e\" is already Everything's keyword.", shown.Window.EngineProblem.Text);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Engines_that_pass_are_saved_and_the_buttons_go_quiet()
    {
        WindowUi.Run(host =>
        {
            using var shown = Shown(new SettingsWindow(host, SettingsSection.WebSearch));
            var window = shown.Window;
            var rows = (IList<SettingsWindow.EngineRow>)window.EngineRows.ItemsSource;

            rows[0].Name = "Google Search";
            window.AddEngineButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            rows[^1].Name = "Ecosia";
            rows[^1].Keyword = "eco";
            rows[^1].Template = "https://www.ecosia.org/search?q={query}";

            Assert.True(window.SaveEngines());

            Assert.Equal(1, host.Applied);
            Assert.Equal("Google Search", host.Settings.WebSearch.Engines[0].Name);
            Assert.Equal("eco", host.Settings.WebSearch.Engines[^1].Keyword);
            Assert.False(window.SaveEnginesButton.IsEnabled);
            Assert.Equal(Visibility.Collapsed, window.EngineProblem.Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Recording_lets_go_of_the_shortcut_and_escape_takes_it_back()
    {
        WindowUi.Run(host =>
        {
            using var shown = Shown(new SettingsWindow(host, SettingsSection.General));
            var window = shown.Window;

            window.ChangeHotkeyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(window.IsRecordingHotkey);
            Assert.Equal(["suspend"], host.HotkeyCalls);

            Press(window, Key.Escape);

            Assert.False(window.IsRecordingHotkey);
            Assert.Equal(["suspend", "resume"], host.HotkeyCalls);
            Assert.Equal("Alt+Space", window.HotkeyText.Text);
            Assert.True(window.IsVisible, "Escape while recording cancels the recording, not the window.");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void A_key_with_no_modifier_is_refused_and_recording_goes_on()
    {
        WindowUi.Run(host =>
        {
            using var shown = Shown(new SettingsWindow(host, SettingsSection.General));
            var window = shown.Window;
            window.ChangeHotkeyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Press(window, Key.K);

            Assert.True(window.IsRecordingHotkey);
            Assert.Equal(Visibility.Visible, window.HotkeyProblem.Visibility);
            Assert.Contains("Ctrl, Alt or Win", window.HotkeyProblem.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(host.HotkeyCalls, c => c.StartsWith("try", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Moving_to_another_section_ends_the_recording_and_takes_the_shortcut_back()
    {
        WindowUi.Run(host =>
        {
            using var shown = Shown(new SettingsWindow(host, SettingsSection.General));
            var window = shown.Window;
            window.ChangeHotkeyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            window.Nav.SelectedIndex = 2;

            Assert.False(window.IsRecordingHotkey);
            Assert.Equal(["suspend", "resume"], host.HotkeyCalls);
            Assert.Equal(SettingsSection.WebSearch, window.Section);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Escape_does_not_close_the_window_over_unsaved_engines()
    {
        WindowUi.Run(host =>
        {
            using var shown = Shown(new SettingsWindow(host, SettingsSection.WebSearch));
            var window = shown.Window;
            ((IList<SettingsWindow.EngineRow>)window.EngineRows.ItemsSource)[0].Name = "Changed";

            Press(window, Key.Escape);

            Assert.True(window.IsVisible);
            Assert.Contains("not saved", window.EngineProblem.Text, StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Closing_the_window_while_recording_takes_the_shortcut_back()
    {
        WindowUi.Run(host =>
        {
            var window = new SettingsWindow(host, SettingsSection.General);
            using (Shown(window))
            {
                window.ChangeHotkeyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }

            Assert.Equal(["suspend", "resume"], host.HotkeyCalls);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void The_update_card_follows_the_updater()
    {
        WindowUi.Run(async host =>
        {
            using var shown = Shown(new SettingsWindow(host, SettingsSection.Updates));
            var window = shown.Window;

            Assert.Equal("You have Hail 1.0.0.", window.UpdateHeadline.Text);
            Assert.Equal("Check now", window.UpdatePrimaryButton.Content);

            host.Service.Latest = new LatestRelease(ReleaseVersion.TryParse("1.2.0")!, new Uri("https://github.com/HendrikVrey/Hail/releases/download/v1.2.0/Hail-Setup.exe"), 40L * 1024 * 1024, new byte[32]);
            await host.Updates.CheckNowAsync();

            Assert.Equal("Hail 1.2.0 is available. You have 1.0.0.", window.UpdateHeadline.Text);
            Assert.Equal("Update", window.UpdatePrimaryButton.Content);
            Assert.Equal(Visibility.Visible, window.UpdateTertiaryButton.Visibility);

            window.UpdateTertiaryButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Contains("skipped", window.UpdateMessage.Text, StringComparison.Ordinal);
            Assert.Equal("Check now", window.UpdatePrimaryButton.Content);
        });
    }

    [Fact]
    public void The_update_switch_is_saved_as_an_answer()
    {
        WindowUi.Run(host =>
        {
            using var shown = Shown(new SettingsWindow(host, SettingsSection.Updates));
            Assert.False(shown.Window.UpdateToggle.IsChecked);

            shown.Window.UpdateToggle.IsChecked = true;
            shown.Window.UpdateToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.True(host.Settings.CheckForUpdates);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Clearing_history_asks_the_host_and_says_so()
    {
        WindowUi.Run(host =>
        {
            host.History = 12;
            using var shown = Shown(new SettingsWindow(host, SettingsSection.History));
            Assert.Equal("12 picks remembered", shown.Window.HistoryCountText.Text);

            shown.Window.ClearHistoryButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(0, host.History);
            Assert.Equal("History cleared", shown.Window.HistoryCountText.Text);
            Assert.False(shown.Window.ClearHistoryButton.IsEnabled);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void A_start_with_windows_switched_off_in_task_manager_says_so()
    {
        WindowUi.Run(host =>
        {
            host.Startup = StartupState.DisabledByUser;
            using var shown = Shown(new SettingsWindow(host, SettingsSection.General));

            Assert.False(shown.Window.StartupToggle.IsChecked);
            Assert.Contains("Task Manager", shown.Window.StartupHint.Text, StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    /// <summary>Shows the window off screen and unactivated, and closes it when disposed.</summary>
    private static ShownWindow Shown(SettingsWindow window)
    {
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.Show();
        return new ShownWindow(window);
    }

    private static void Press(Window window, Key key)
    {
        var source = PresentationSource.FromVisual(window)!;
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });
    }

    private static T Find<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T found)
            {
                return found;
            }

            if (FindOrNull<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        throw new InvalidOperationException($"No {typeof(T).Name} under {root}.");
    }

    private static T? FindOrNull<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T found)
            {
                return found;
            }

            if (FindOrNull<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    private sealed class ShownWindow(SettingsWindow window) : IDisposable
    {
        public SettingsWindow Window { get; } = window;

        public void Dispose() => Window.Close();
    }
}

/// <summary>A host that records what the window asked of it.</summary>
internal sealed class FakeSettingsHost : ISettingsHost
{
    public FakeSettingsHost()
    {
        Updates = new UpdateCoordinator(Service, new MemoryUpdateStore(), ReleaseVersion.TryParse("1.0.0")!);
    }

    public event Action? PluginsChanged;

    public HailSettings Settings { get; private set; } = HailSettings.Default;

    public string Version => "1.0.0";

    public bool HotkeyRegistered { get; private set; } = true;

    public IReadOnlyList<BuiltInProvider> BuiltInProviders =>
    [
        new("hail.apps", "Apps", "Apps.", []),
        new("hail.files", "Files", "Files.", []),
        new("hail.calculator", "Calculator", "Sums.", ["="]),
    ];

    public Dictionary<string, string> Taken { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> KeywordsOutsideWebSearch => Taken;

    public IReadOnlyList<PluginEntry> Plugins => [];

    public int History { get; set; }

    public int HistoryCount => History;

    public FakeUpdateService Service { get; } = new();

    public UpdateCoordinator Updates { get; }

    public StartupState Startup { get; set; } = StartupState.Off;

    public StartupState StartupState => Startup;

    public bool CanRecordPluginAnswers => true;

    public int Applied { get; private set; }

    public List<string> HotkeyCalls { get; } = [];

    public string? Apply(HailSettings next)
    {
        Applied++;
        Settings = next;
        return null;
    }

    public void SuspendHotkey()
    {
        HotkeyCalls.Add("suspend");
        HotkeyRegistered = false;
    }

    public string? TryHotkey(Chord chord)
    {
        HotkeyCalls.Add($"try {chord.Display}");
        Settings = Settings with { Hotkey = chord };
        HotkeyRegistered = true;
        return null;
    }

    public void ResumeHotkey()
    {
        HotkeyCalls.Add("resume");
        HotkeyRegistered = true;
    }

    public string? SetStartup(bool enabled)
    {
        Startup = enabled ? StartupState.On : StartupState.Off;
        return null;
    }

    public string DescribePlugin(PluginEntry entry) => "On";

    public void AskAboutPlugin(PluginEntry entry)
    {
    }

    public void SwitchOffPlugin(PluginEntry entry)
    {
    }

    public void EditPluginSettings(PluginEntry entry)
    {
    }

    public void ReloadPlugins() => PluginsChanged?.Invoke();

    public void OpenPluginsFolder()
    {
    }

    public void ClearHistory() => History = 0;

    public Task InstallUpdateAsync() => Updates.InstallAsync();

    public void OpenDataFolder()
    {
    }

    public void OpenReleasesPage()
    {
    }

    public void OpenProjectPage()
    {
    }

    private sealed class MemoryUpdateStore : IUpdateStateStore
    {
        private UpdateState _state = UpdateState.Empty;

        public UpdateState Load() => _state;

        public bool Save(UpdateState state)
        {
            _state = state;
            return true;
        }
    }
}

internal sealed class FakeUpdateService : IUpdateService
{
    public LatestRelease? Latest { get; set; }

    public Task<LatestRelease> GetLatestAsync(CancellationToken ct) =>
        Latest is null ? Task.FromException<LatestRelease>(new UpdateCheckException("No version of Hail has been released yet.")) : Task.FromResult(Latest);

    public Task<DownloadedInstaller> DownloadAsync(LatestRelease release, IProgress<double>? progress, CancellationToken ct) =>
        Task.FromResult(new DownloadedInstaller("setup.exe", "folder", release.Sha256));

    public void Launch(DownloadedInstaller installer)
    {
    }

    public void Discard(DownloadedInstaller installer)
    {
    }

    public void SweepOldDownloads()
    {
    }
}

/// <summary>
/// Runs window tests on one long-lived STA thread that owns a real <see cref="App"/> with its
/// resources, as the running Hail does: a window needs the application's styles and fonts, and a
/// WPF resource belongs to the thread that made it.
/// </summary>
internal static class WindowUi
{
    private static readonly Lazy<Dispatcher> Thread = new(StartThread);

    public static void Run(Func<FakeSettingsHost, Task> body)
    {
        var done = Thread.Value.InvokeAsync(() => body(new FakeSettingsHost())).Task.Unwrap();
        if (!done.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("The window test did not finish on its dispatcher thread.");
        }

        if (done.Exception is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure.InnerException ?? failure);
        }
    }

    private static Dispatcher StartThread()
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                ready.SetResult(Dispatcher.CurrentDispatcher);
            }
            catch (Exception ex)
            {
                ready.SetException(ex);
                return;
            }

            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Hail window tests",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
