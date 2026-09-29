using Hail.Core.Ports;
using Hail.Windows.Apps;
using Hail.Windows.Icons;

namespace Hail.Windows.Tests;

/// <summary>
/// Hail.md §12's M0 probes of the shell, run against this machine's own Start menu.
/// </summary>
public sealed class ShellProbeTests : IDisposable
{
    private readonly StaWorker _worker = new("test shell worker");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _worker.Dispose();

    [Fact]
    public async Task The_apps_folder_lists_desktop_and_packaged_apps_in_one_enumeration()
    {
        var catalog = new ShellAppCatalog(_worker);
        Assert.True(catalog.IsStale);

        var refresh = await catalog.RefreshAsync(Token);

        Assert.NotNull(refresh);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{refresh.Count} apps, {refresh.Packaged} packaged, in {refresh.Elapsed.TotalMilliseconds:0} ms");

        Assert.False(catalog.IsStale);
        Assert.Equal(refresh.Count, catalog.Apps.Count);
        Assert.True(refresh.Count > 0, "The AppsFolder listed nothing.");

        // Every Windows 11 desktop has packaged apps (Settings, Calculator) and desktop ones.
        Assert.True(refresh.Packaged > 0, "No packaged apps: the enumeration missed the MSIX half.");
        Assert.True(refresh.Count > refresh.Packaged, "No desktop apps: the enumeration missed the shortcut half.");
        Assert.All(catalog.Apps, app =>
        {
            Assert.False(string.IsNullOrWhiteSpace(app.Id));
            Assert.False(string.IsNullOrWhiteSpace(app.Name));
            Assert.True(catalog.Contains(app.Id));
        });
    }

    [Fact]
    public async Task An_apps_icon_renders_at_the_size_asked_off_the_ui_thread()
    {
        var catalog = new ShellAppCatalog(_worker);
        await catalog.RefreshAsync(Token);
        var app = catalog.Apps.First(a => ShellAppCatalog.IsPackaged(a.Id));

        var pixels = await new ShellIcons(_worker).LoadAsync(app.ShellPath, 48, Token);

        Assert.NotNull(pixels);
        Assert.Equal(48, pixels.Width);
        Assert.Equal(48, pixels.Height);
        Assert.Equal(48 * 48 * 4, pixels.Bgra.Length);
        Assert.Contains(pixels.Bgra.Where((_, i) => i % 4 == 3), alpha => alpha != 0);
    }

    [Fact]
    public async Task A_desktop_apps_icon_renders_too()
    {
        var catalog = new ShellAppCatalog(_worker);
        await catalog.RefreshAsync(Token);
        var app = catalog.Apps.First(a => !ShellAppCatalog.IsPackaged(a.Id));

        Assert.NotNull(await new ShellIcons(_worker).LoadAsync(app.ShellPath, 32, Token));
    }

    [Fact]
    public async Task A_name_the_shell_cannot_resolve_has_no_icon_rather_than_an_error()
    {
        var pixels = await new ShellIcons(_worker).LoadAsync(AppsFolder.PathFor("No.Such.App_0000000000000!Nothing"), 32, Token);
        Assert.Null(pixels);
    }

    [Fact]
    public async Task The_launcher_refuses_an_id_the_catalog_did_not_list()
    {
        var catalog = new ShellAppCatalog(_worker);
        await catalog.RefreshAsync(Token);
        var launcher = new ShellAppLauncher(catalog);

        await Assert.ThrowsAsync<LaunchRefusedException>(() => launcher.LaunchAppAsync(@"C:\Windows\System32\cmd.exe", Token).AsTask());
        await Assert.ThrowsAsync<LaunchRefusedException>(() => launcher.LaunchAppAsync("", Token).AsTask());
    }

    [Fact]
    public async Task Work_cancelled_before_it_starts_never_runs()
    {
        var ran = false;
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _worker.RunAsync(() => ran = true, cancel.Token));
        Assert.False(ran);
    }

    [Fact]
    public async Task The_worker_runs_in_a_single_threaded_apartment()
    {
        Assert.Equal(ApartmentState.STA, await _worker.RunAsync(() => Thread.CurrentThread.GetApartmentState(), Token));
    }

    [Fact]
    public void An_icon_with_no_alpha_at_all_is_opaque_not_invisible()
    {
        var pixels = new byte[] { 10, 20, 30, 0, 40, 50, 60, 0 };
        var icon = ShellIcons.WithAlpha(2, 1, pixels);

        Assert.False(icon.Premultiplied);
        Assert.Equal([10, 20, 30, 255, 40, 50, 60, 255], icon.Bgra);
    }

    [Fact]
    public void An_icon_with_alpha_keeps_it()
    {
        var icon = ShellIcons.WithAlpha(2, 1, [10, 20, 30, 0, 40, 50, 60, 128]);

        Assert.True(icon.Premultiplied);
        Assert.Equal(0, icon.Bgra[3]);
    }
}
