using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Hail.Core.Tests;

/// <summary>
/// The layering rules of Hail.md §11, checked against the project files and sources on disk.
/// </summary>
/// <remarks>
/// On disk rather than against loaded assemblies, as Sling's, Signet's and Drift's are: an
/// assembly only references what its IL uses, so an assembly check passes silently while a
/// project is still thin, which is exactly when these rules are cheapest to keep.
/// </remarks>
public sealed partial class ArchitectureTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static readonly string[] NonInteropProjects = ["Hail.Sdk", "Hail.Core", "Hail.Providers", "Hail.Persistence", "Hail.Plugins", "Hail.Updates", "Hail.App"];

    [Fact]
    public void The_sdk_depends_on_nothing()
    {
        var project = XDocument.Load(ProjectFile("Hail.Sdk"));
        var references = project.Descendants("PackageReference").Concat(project.Descendants("ProjectReference"))
            .Select(e => e.Attribute("Include")?.Value ?? "(unnamed)")
            .ToArray();

        Assert.True(
            references.Length == 0,
            "Hail.Sdk is the one assembly a plugin author references; it must stand alone, "
                + $"but declares: {string.Join(", ", references)}.");
    }

    [Fact]
    public void The_sdk_names_no_ui_type()
    {
        AssertNoMatch(
            "Hail.Sdk",
            UiTypes(),
            "A plugin must not care which UI framework the host uses (Hail.md §6.2); icons are data.");
    }

    [Fact]
    public void The_sdk_carries_its_own_mit_licence()
    {
        var licence = File.ReadAllText(Path.Combine(RepoRoot, "src", "Hail.Sdk", "LICENSE"));
        Assert.StartsWith("MIT License", licence, StringComparison.Ordinal);
        Assert.Contains(
            "<PackageLicenseExpression>MIT</PackageLicenseExpression>",
            File.ReadAllText(ProjectFile("Hail.Sdk")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_everything_plugin_is_mit_and_carries_its_licence()
    {
        var folder = Path.Combine(RepoRoot, "plugins", "Hail.Plugin.Everything");

        // Hendrik, 2026-09-30: the reference plugin is an example people copy from.
        Assert.Equal(
            File.ReadAllText(Path.Combine(RepoRoot, "src", "Hail.Sdk", "LICENSE")),
            File.ReadAllText(Path.Combine(folder, "LICENSE")));
        Assert.Contains("<None Include=\"LICENSE\" CopyToOutputDirectory", File.ReadAllText(Path.Combine(folder, "Hail.Plugin.Everything.csproj")), StringComparison.Ordinal);
        Assert.Contains("plugins/Hail.Plugin.Everything", File.ReadAllText(Path.Combine(RepoRoot, "LICENSE")), StringComparison.Ordinal);
    }

    [Fact]
    public void Core_declares_no_package_reference()
    {
        Assert.Empty(XDocument.Load(ProjectFile("Hail.Core")).Descendants("PackageReference"));
    }

    [Fact]
    public void Core_does_no_io_starts_no_process_and_names_no_ui()
    {
        AssertNoMatch(
            "Hail.Core",
            new Regex(@"\b(File|Directory|FileStream|FileInfo|Registry)\s*\.|\bProcess\b|" + UiTypes().ToString()),
            "Hail.Core is pure (Hail.md §11): what it decides is tested without a disk, a process or a window.");
    }

    [Fact]
    public void Providers_reach_the_host_only_through_the_sdk_and_ports()
    {
        AssertNoMatch(
            "Hail.Providers",
            HostInternals(),
            "A first-party provider uses Hail.Sdk and Hail.Core.Ports only, exactly as a plugin "
                + "would; if it needs more, the SDK grows (Hail.md §6.1).");
    }

    [Fact]
    public void Providers_start_no_process_and_touch_no_disk()
    {
        AssertNoMatch(
            "Hail.Providers",
            new Regex(@"\bProcess\b|\b(File|Directory|FileStream)\s*\."),
            "Launching goes through ILauncher, where the host's rules apply (Hail.md §9).");
    }

    [Theory]
    [MemberData(nameof(NonInterop))]
    public void Only_hail_windows_calls_native_code(string project)
    {
        AssertNoMatch(
            project,
            new Regex(@"\[(DllImport|LibraryImport|ComImport)\b|\bextern\s+\w"),
            "Every P/Invoke and COM declaration lives in Hail.Windows, so the interop is reviewed in one place.");
    }

    [Fact]
    public void Only_the_launcher_and_the_installer_start_a_process()
    {
        // The launcher starts what the user picked, by the host's rules; InstallerFiles starts
        // exactly one thing, an installer Hail downloaded and has just verified again (Hail.md §9).
        var launcher = Path.Combine("src", "Hail.Windows", "Launching", "ShellLauncher.cs");
        var installer = Path.Combine("src", "Hail.Windows", "Updates", "InstallerFiles.cs");
        var offenders = SourceFiles(Path.Combine(RepoRoot, "src"))
            .Where(f => !f.EndsWith(launcher, StringComparison.OrdinalIgnoreCase) && !f.EndsWith(installer, StringComparison.OrdinalIgnoreCase))
            .Where(f => ProcessStart().IsMatch(CodeOnly(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(RepoRoot, f))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Only {launcher} and {installer} may start a process (Hail.md §9). Offending files: {string.Join("; ", offenders)}");
        Assert.Matches(ProcessStart(), File.ReadAllText(Path.Combine(RepoRoot, launcher)));
        Assert.Single(Regex.Matches(CodeOnly(File.ReadAllText(Path.Combine(RepoRoot, installer))), @"\bProcess\.Start\("));
    }

    [Fact]
    public void Only_the_update_check_speaks_to_the_network()
    {
        // Hail.md §9: nothing typed leaves the machine, and the one request Hail makes itself is
        // the opt-in update check. A plugin can do what it likes; Hail's own code cannot.
        var updates = Path.Combine("src", "Hail.Updates");
        var offenders = SourceFiles(Path.Combine(RepoRoot, "src"))
            .Where(f => !Path.GetRelativePath(RepoRoot, f).StartsWith(updates + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(f => Network().IsMatch(CodeOnly(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(RepoRoot, f))
            .ToArray();

        Assert.True(offenders.Length == 0, $"Only {updates} may reach the network. Offending files: {string.Join("; ", offenders)}");
        Assert.Contains(
            "https://api.github.com/repos/HendrikVrey/Hail/releases/latest",
            File.ReadAllText(Path.Combine(RepoRoot, updates, "UpdateClient.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_app_names_no_process_no_load_context_and_no_oledb()
    {
        AssertNoMatch(
            "Hail.App",
            new Regex(@"\bProcess\b|\bAssemblyLoadContext\b|\bOleDb"),
            "Hail.App draws and wires; starting, loading and querying belong to the layers below it.");
    }

    [Fact]
    public void Only_the_search_index_speaks_oledb_and_only_with_sql_from_the_escaping_function()
    {
        var index = Path.Combine("src", "Hail.Windows", "Files", "WindowsSearchIndex.cs");
        var offenders = SourceFiles(Path.Combine(RepoRoot, "src"))
            .Where(f => !f.EndsWith(index, StringComparison.OrdinalIgnoreCase))
            .Where(f => OleDb().IsMatch(CodeOnly(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(RepoRoot, f))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Only {index} may reach the index (Hail.md §7.2). Offending files: {string.Join("; ", offenders)}");

        // The one command it builds is built from WindowsSearchSql's text and nothing else.
        var source = CodeOnly(File.ReadAllText(Path.Combine(RepoRoot, index)));
        Assert.Single(Regex.Matches(source, @"new OleDbCommand\("));
        Assert.Contains("new OleDbCommand(sql, connection)", source, StringComparison.Ordinal);
        Assert.Matches(@"var sql = WindowsSearchSql\.FileNameQuery\(", source);
    }

    [Fact]
    public void Only_start_at_sign_in_touches_the_registry()
    {
        var startup = Path.Combine("src", "Hail.Windows", "Startup", "StartupRegistration.cs");
        var offenders = SourceFiles(Path.Combine(RepoRoot, "src"))
            .Where(f => !f.EndsWith(startup, StringComparison.OrdinalIgnoreCase))
            .Where(f => Regex.IsMatch(CodeOnly(File.ReadAllText(f)), @"\bRegistry(Key)?\b"))
            .Select(f => Path.GetRelativePath(RepoRoot, f))
            .ToArray();

        Assert.True(offenders.Length == 0, $"Only {startup} writes to the registry. Offending files: {string.Join("; ", offenders)}");
    }

    [Fact]
    public void Only_the_plugin_host_loads_code()
    {
        var host = Path.Combine("src", "Hail.Plugins");
        var offenders = SourceFiles(Path.Combine(RepoRoot, "src"))
            .Where(f => !Path.GetRelativePath(RepoRoot, f).StartsWith(host + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(f => LoadsCode().IsMatch(CodeOnly(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(RepoRoot, f))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Only {host} may load an assembly (Hail.md §6.3), so every plugin goes through its checks. Offending files: {string.Join("; ", offenders)}");
    }

    [Fact]
    public void The_plugin_host_starts_no_process_and_touches_no_registry()
    {
        AssertNoMatch(
            "Hail.Plugins",
            new Regex(@"\bProcess\b|\bRegistry(Key)?\b"),
            "Hail.Plugins loads and unloads; launching belongs to the launcher, whatever a plugin asks.");
    }

    [Fact]
    public void The_everything_plugin_references_the_sdk_and_nothing_else_of_hails()
    {
        var project = XDocument.Load(Path.Combine(RepoRoot, "plugins", "Hail.Plugin.Everything", "Hail.Plugin.Everything.csproj"));
        var references = project.Descendants("ProjectReference").Select(e => e.Attribute("Include")!.Value).ToArray();

        // Hail.md §11: if the SDK is not enough to write it, the SDK is wrong.
        var sdk = Assert.Single(references);
        Assert.EndsWith(@"src\Hail.Sdk\Hail.Sdk.csproj", sdk, StringComparison.Ordinal);
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Equal("false", project.Descendants("ProjectReference").Single().Attribute("Private")?.Value);
    }

    [Fact]
    public void The_sdk_is_packable_with_its_readme_and_nothing_else_is()
    {
        var sdk = File.ReadAllText(ProjectFile("Hail.Sdk"));
        Assert.Contains("<IsPackable>true</IsPackable>", sdk, StringComparison.Ordinal);
        Assert.Contains("<PackageReadmeFile>README.md</PackageReadmeFile>", sdk, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(RepoRoot, "src", "Hail.Sdk", "README.md")));
        Assert.Contains("<IsPackable>false</IsPackable>", File.ReadAllText(Path.Combine(RepoRoot, "Directory.Build.props")), StringComparison.Ordinal);
    }

    public static TheoryData<string> NonInterop() => new(NonInteropProjects);

    [GeneratedRegex(@"\bAssemblyLoadContext\b|\bAssembly\.(Load|LoadFrom|LoadFile|UnsafeLoadFrom)\b|\bLoadFromAssemblyPath\b|\bLoadFromStream\b|\bActivator\.CreateInstance\b")]
    private static partial Regex LoadsCode();

    [GeneratedRegex(@"\bHttpClient\b|\bHttpMessageHandler\b|\bSocketsHttpHandler\b|\bWebRequest\b|\bWebClient\b|\bTcpClient\b|\bUdpClient\b|\bSockets?\.")]
    private static partial Regex Network();

    [GeneratedRegex(@"\bOleDb\w*\b")]
    private static partial Regex OleDb();

    [GeneratedRegex(@"\bSystem\.Windows\b|\bWpf\.Ui\b|\bImageSource\b|\bBitmapSource\b")]
    private static partial Regex UiTypes();

    [GeneratedRegex(@"\bHail\.(Core\.(?!Ports\b)\w+|Windows|Persistence|App)\b")]
    private static partial Regex HostInternals();

    [GeneratedRegex(@"\bProcess\.Start\b|\bProcessStartInfo\b")]
    private static partial Regex ProcessStart();

    private static void AssertNoMatch(string project, Regex forbidden, string why)
    {
        var offenders = SourceFiles(Path.Combine(RepoRoot, "src", project))
            .Select(f => (Relative: Path.GetRelativePath(RepoRoot, f), Match: forbidden.Match(CodeOnly(File.ReadAllText(f)))))
            .Where(x => x.Match.Success)
            .Select(x => $"{x.Relative} ('{x.Match.Value}')")
            .ToArray();

        Assert.True(offenders.Length == 0, $"{why}{Environment.NewLine}Offending files: {string.Join("; ", offenders)}");
    }

    private static IEnumerable<string> SourceFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>
    /// Drops whole-line comments, so documentation that names a forbidden thing to explain why
    /// it is forbidden does not trip the rule (Sling's lesson: a check that punishes a file for
    /// explaining itself teaches people to stop explaining).
    /// </summary>
    private static string CodeOnly(string source) =>
        string.Join('\n', source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string ProjectFile(string project) => Path.Combine(RepoRoot, "src", project, $"{project}.csproj");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hail.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Could not find Hail.slnx above '{AppContext.BaseDirectory}'.");
    }
}
