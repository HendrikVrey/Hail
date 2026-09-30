using Hail.Core.Ports;
using Hail.Providers.Files;
using Hail.Sdk;
using static Hail.Providers.Tests.Harness;

namespace Hail.Providers.Tests;

public sealed class FilesProviderTests
{
    private const string Home = @"C:\Users\Test";

    private readonly FakeIndex _index = new();
    private readonly FakeFiles _files = new();
    private readonly FakeLauncher _launcher = new();
    private readonly FakeClipboard _clipboard = new();

    private async Task<FilesProvider> StartedAsync()
    {
        var provider = new FilesProvider(_index, _files);
        await provider.InitializeAsync(Context(_launcher, _clipboard), Token);
        return provider;
    }

    private async Task<List<Result>> FindAsync(string text) => await CollectAsync(await StartedAsync(), Query.Global(text));

    [Fact]
    public async Task A_name_goes_to_the_index_and_comes_back_matched_and_bolded()
    {
        _index.Answer = new FileIndexAnswer.Found(
        [
            new(@"C:\Users\Test\Documents\annual report.pdf", "annual report.pdf", IsFolder: false),
            new(@"C:\Users\Test\Documents\unrelated.txt", "unrelated.txt", IsFolder: false),
        ]);

        var result = Assert.Single(await FindAsync("report"));

        Assert.Equal(["report"], _index.Asked);
        Assert.Equal("annual report.pdf", result.Title);
        Assert.Equal(@"C:\Users\Test\Documents", result.Subtitle);
        Assert.NotNull(result.Highlight);
        Assert.True(result.Relevance <= 0.8, "A file ranks below an app matched as well.");
    }

    [Fact]
    public async Task A_start_menu_shortcut_is_left_to_the_apps_provider()
    {
        _index.Answer = new FileIndexAnswer.Found(
        [
            new(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Notepad++.lnk", "Notepad++.lnk", IsFolder: false),
            new(@"C:\Users\Test\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Tool.lnk", "Tool.lnk", IsFolder: false),
            new(@"C:\Users\Test\Documents\notepad tips.txt", "notepad tips.txt", IsFolder: false),
        ]);

        Assert.Equal(["notepad tips.txt"], (await FindAsync("notepad")).Select(r => r.Title));
    }

    [Theory]
    [InlineData("r")]
    [InlineData(@"\\server\share")]
    [InlineData(@"C:\a\..\Windows")]
    public async Task Too_short_or_a_path_it_will_not_list_asks_the_index_nothing(string text)
    {
        Assert.Empty(await FindAsync(text));
        Assert.Empty(_index.Asked);
    }

    [Fact]
    public async Task When_windows_search_is_off_it_says_so_as_a_last_resort()
    {
        _index.Answer = new FileIndexAnswer.Unavailable("Windows Search is not answering.");

        var result = Assert.Single(await FindAsync("report"));

        Assert.Equal("Windows Search is not answering.", result.Title);
        Assert.True(result.Relevance <= Result.LastResort);
        Assert.Equal(ActionOutcome.KeepOpen, await Run(result.Primary));
    }

    [Fact]
    public async Task A_path_lists_the_folder_folders_first_and_never_asks_the_index()
    {
        _files.Folders[@"C:\Users\"] = [new(@"C:\Users\Test", "Test", IsFolder: true), new(@"C:\Users\notes.txt", "notes.txt", IsFolder: false)];

        var results = await FindAsync(@"C:\Users\");

        Assert.Empty(_index.Asked);
        Assert.Equal(2, results.Count);
        Assert.True(results.Single(r => r.Title == "Test").Relevance > results.Single(r => r.Title == "notes.txt").Relevance);
        Assert.Equal(@"C:\Users\Test\", results.Single(r => r.Title == "Test").Completion);
        Assert.Equal(@"C:\Users\notes.txt", results.Single(r => r.Title == "notes.txt").Completion);
    }

    [Fact]
    public async Task What_follows_the_last_separator_narrows_the_listing()
    {
        _files.Folders[@"C:\Users\"] = [new(@"C:\Users\Test", "Test", IsFolder: true), new(@"C:\Users\Public", "Public", IsFolder: true)];

        var result = Assert.Single(await FindAsync("C:/Users/pub"));

        Assert.Equal("Public", result.Title);
        Assert.Equal([0, 1, 2], result.Highlight!.Positions());
    }

    [Fact]
    public async Task A_tilde_is_the_home_folder()
    {
        _files.Folders[@"C:\Users\Test\"] = [new(@"C:\Users\Test\Documents", "Documents", IsFolder: true)];

        Assert.Equal("Documents", Assert.Single(await FindAsync(@"~\doc")).Title);
    }

    [Fact]
    public async Task A_file_opens_shows_in_its_folder_and_copies_its_path_or_itself()
    {
        const string path = @"C:\Users\Test\notes.txt";
        _files.Folders[@"C:\Users\Test\"] = [new(path, "notes.txt", IsFolder: false)];
        var result = Assert.Single(await FindAsync(@"C:\Users\Test\no"));

        await Run(result.Primary);
        await Run(On(result, Gesture.CtrlEnter));
        await Run(On(result, Gesture.CtrlC));
        await Run(On(result, Gesture.CtrlShiftC));

        Assert.Equal([$"open {path}", $"show {path}"], _launcher.Calls);
        Assert.Equal([$"text {path}", $"file {path}"], _clipboard.Calls);
    }

    [Fact]
    public async Task A_folder_opens_and_copies_its_path_only()
    {
        _files.Folders[@"C:\"] = [new(@"C:\Tools", "Tools", IsFolder: true)];
        var result = Assert.Single(await FindAsync(@"C:\to"));

        Assert.Equal([Gesture.CtrlC], result.Secondary.Select(a => a.Gesture));
    }

    [Fact]
    public async Task A_remembered_file_comes_back_while_it_exists()
    {
        const string path = @"C:\Users\Test\notes.txt";
        _files.Items[path] = new LocalItem(path, "notes.txt", IsFolder: false);
        var provider = await StartedAsync();

        Assert.Equal("notes.txt", Assert.IsType<Recollection.Found>(await provider.RecallAsync(path, Token)).Result.Title);
        Assert.Equal(Recollection.Gone, await provider.RecallAsync(@"C:\deleted.txt", Token));
    }

    [Fact]
    public async Task A_file_on_a_drive_that_is_not_there_is_not_forgotten()
    {
        var provider = await StartedAsync();

        Assert.Equal(Recollection.Unknown, await provider.RecallAsync(@"E:\on the usb stick.txt", Token));
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData("c:/users", true)]
    [InlineData("~", true)]
    [InlineData(@"~\x", true)]
    [InlineData(@"\\server\x", true)]
    [InlineData("C:", false)]
    [InlineData("notes", false)]
    [InlineData("~x", false)]
    public void Knows_a_path_when_it_sees_one(string text, bool pathLike)
    {
        Assert.Equal(pathLike, PathMode.IsPathLike(text));
    }

    private sealed class FakeIndex : IFileIndex
    {
        public FileIndexAnswer Answer { get; set; } = new FileIndexAnswer.Found([]);

        public List<string> Asked { get; } = [];

        public Task<FileIndexAnswer> SearchAsync(string search, int limit, CancellationToken ct)
        {
            Asked.Add(search);
            return Task.FromResult(Answer);
        }
    }

    private sealed class FakeFiles : ILocalFiles
    {
        public Dictionary<string, IReadOnlyList<LocalItem>> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, LocalItem> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string HomeFolder => Home;

        public FolderListing List(string folder, int max, CancellationToken ct) =>
            Folders.TryGetValue(folder, out var items) ? new FolderListing([.. items.Take(max)], Exists: true) : FolderListing.Missing;

        public LocalItem? Describe(string path) => Items.GetValueOrDefault(path);

        public bool IsDrivePresent(string path) => path.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase);
    }
}
