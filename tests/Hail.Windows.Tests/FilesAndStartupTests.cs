using Hail.Core.Ports;
using Hail.Windows.Files;
using Hail.Windows.Launching;
using Hail.Windows.Startup;
using Microsoft.Win32;

namespace Hail.Windows.Tests;

/// <summary>The files provider's two sources and start at sign-in, against this machine.</summary>
public sealed class FilesAndStartupTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HailTests", Guid.NewGuid().ToString("N"));
    private readonly string _testKey = $@"Software\Hail.Tests\{Guid.NewGuid():N}";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        Registry.CurrentUser.DeleteSubKeyTree(_testKey, throwOnMissingSubKey: false);
    }

    [Theory]
    [InlineData(@"C:\Windows\notepad.exe", true)]
    [InlineData(@"d:\x", true)]
    [InlineData(@"\\server\share\x", false)]
    [InlineData(@"\\?\C:\x", false)]
    [InlineData(@"\\.\PhysicalDrive0", false)]
    [InlineData(@"C:relative", false)]
    [InlineData(@"C:/forward", false)]
    [InlineData(@"C:\a.txt:hidden", false)]
    [InlineData("relative", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_full_paths_on_a_drive_letter_are_acceptable(string? path, bool acceptable)
    {
        Assert.Equal(acceptable, LocalPaths.IsAcceptable(path));
    }

    [Fact]
    public void A_folder_is_listed_without_its_hidden_entries()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "sub"));
        File.WriteAllText(Path.Combine(_folder, "seen.txt"), "x");
        var hidden = Path.Combine(_folder, "hidden.txt");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var listing = new LocalFiles().List(_folder + @"\", 100, Token);

        Assert.True(listing.Exists);
        Assert.Equal(["seen.txt", "sub"], listing.Items.Select(i => i.Name).Order(StringComparer.Ordinal));
        Assert.True(listing.Items.Single(i => i.Name == "sub").IsFolder);
    }

    [Fact]
    public void A_listing_stops_at_its_limit()
    {
        Directory.CreateDirectory(_folder);
        for (var i = 0; i < 10; i++)
        {
            File.WriteAllText(Path.Combine(_folder, $"{i}.txt"), "x");
        }

        Assert.Equal(3, new LocalFiles().List(_folder, 3, Token).Items.Count);
    }

    [Theory]
    [InlineData(@"C:\No such folder at all\")]
    [InlineData(@"\\server\share\")]
    public void A_folder_that_is_not_there_or_not_local_is_missing(string folder)
    {
        Assert.False(new LocalFiles().List(folder, 10, Token).Exists);
    }

    [Fact]
    public void A_remembered_file_is_described_while_it_exists()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(path, "x");
        var files = new LocalFiles();

        Assert.Equal(new LocalItem(path, "notes.txt", IsFolder: false), files.Describe(path));

        File.Delete(path);
        Assert.Null(files.Describe(path));
    }

    [Fact]
    public async Task The_windows_search_index_answers_or_says_it_cannot()
    {
        var answer = await new WindowsSearchIndex().SearchAsync("desktop", 20, Token);

        switch (answer)
        {
            case FileIndexAnswer.Found found:
                TestContext.Current.TestOutputHelper?.WriteLine($"Windows Search answered with {found.Items.Count} items.");
                Assert.All(found.Items, item => Assert.True(LocalPaths.IsAcceptable(item.Path), item.Path));

                // "desktop" finds the hidden desktop.ini of many folders, and rows the index still
                // lists after the file is gone: neither reaches the box.
                Assert.All(found.Items, item => Assert.Equal((FileAttributes)0, File.GetAttributes(item.Path) & (FileAttributes.Hidden | FileAttributes.System)));
                break;
            case FileIndexAnswer.Unavailable unavailable:
                TestContext.Current.TestOutputHelper?.WriteLine($"Windows Search is unavailable here: {unavailable.Reason}");
                break;
        }
    }

    [Fact]
    public async Task A_hostile_search_reaches_the_index_as_a_search()
    {
        // Not a syntax error from the index: the escaping kept it inside its literals.
        var answer = await new WindowsSearchIndex().SearchAsync("x' OR '1'='1 %_[ \"*", 5, Token);

        if (answer is FileIndexAnswer.Found found)
        {
            Assert.All(found.Items, item => Assert.True(LocalPaths.IsAcceptable(item.Path), item.Path));
        }
    }

    [Fact]
    public void Start_at_sign_in_round_trips_through_the_run_key()
    {
        var startup = new StartupRegistration($@"{_testKey}\Run", $@"{_testKey}\Approved", "Hail");
        const string exe = @"C:\Program Files\Hail\Hail.exe";

        Assert.Equal(StartupState.Off, startup.StateFor(exe));

        startup.Enable(exe);
        Assert.Equal(StartupState.On, startup.StateFor(exe));
        Assert.Equal(@"""C:\Program Files\Hail\Hail.exe""", Registry.CurrentUser.OpenSubKey($@"{_testKey}\Run")!.GetValue("Hail"));
        Assert.Equal(StartupState.OnElsewhere, startup.StateFor(@"C:\Other\Hail.exe"));

        startup.Disable();
        Assert.Equal(StartupState.Off, startup.StateFor(exe));
    }

    [Fact]
    public void Switched_off_in_task_manager_is_read_and_switching_on_again_clears_it()
    {
        var startup = new StartupRegistration($@"{_testKey}\Run", $@"{_testKey}\Approved", "Hail");
        const string exe = @"C:\Program Files\Hail\Hail.exe";
        startup.Enable(exe);
        using (var approved = Registry.CurrentUser.CreateSubKey($@"{_testKey}\Approved"))
        {
            approved.SetValue("Hail", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        }

        Assert.Equal(StartupState.DisabledByUser, startup.StateFor(exe));

        startup.Enable(exe);
        Assert.Equal(StartupState.On, startup.StateFor(exe));
    }

    [Theory]
    [InlineData("Hail.exe")]
    [InlineData(@"C:\Hail\Hail.exe"" --evil")]
    public void Only_a_full_path_is_registered(string executable)
    {
        var startup = new StartupRegistration($@"{_testKey}\Run", $@"{_testKey}\Approved", "Hail");
        Assert.Throws<ArgumentException>(() => startup.Enable(executable));
    }
}
