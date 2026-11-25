using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using SimpleFile.Tests.Git;
using SimpleFile.Tests.Remote;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// The helpers the bundled plugins' WinUI assemblies share (<c>plugins\shared\ui</c>) and their shared build files
/// (<c>plugins\Directory.Build.props</c> and <c>.targets</c>).
/// </summary>
public class PluginSharedUiTests
{
    private static readonly (string Name, string Project)[] Plugins =
    [
        ("git", "SumaFile.Git"),
        ("remote", "SumaFile.Remote"),
    ];

    /// <summary>The helpers are plugin UI code like any other, and follow its rules.</summary>
    [Fact]
    public void TheSharedHelpers_FollowThePluginUiRules()
    {
        var files = Directory.GetFiles(SharedUiRoot(), "*.cs");
        Assert.Contains(files, file => Path.GetFileName(file) == "Xaml.cs");
        Assert.Contains(files, file => Path.GetFileName(file) == "ListRows.cs");
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.Contains("namespace SumaFile.Plugins.Ui;", text);
            Assert.Contains("internal static class", text);
            Assert.False(text.Contains("{Binding", StringComparison.Ordinal), $"{file} uses {{Binding}}.");
            Assert.False(text.Contains("x:Bind", StringComparison.Ordinal), $"{file} uses x:Bind.");
            Assert.False(text.Contains("InitializeComponent", StringComparison.Ordinal), $"{file} calls InitializeComponent.");
            Assert.False(text.Contains("{StaticResource", StringComparison.Ordinal), $"{file} uses {{StaticResource}}.");
            Assert.False(text.Contains("GetTempPath", StringComparison.Ordinal), $"{file} resolves the temp folder.");
            Assert.False(text.Contains("SpecialFolder", StringComparison.Ordinal), $"{file} resolves a user folder.");
            Assert.False(text.Contains("DialogHost", StringComparison.Ordinal), $"{file} uses the App's DialogHost.");
            Assert.False(text.Contains(".ShowAsync()", StringComparison.Ordinal), $"{file} shows a dialog around the host gate.");
        }
    }

    /// <summary>
    /// Each plugin compiles the one shared copy. A plugin's assemblies load in its own context, so the helpers are
    /// shared as source; a copy of its own in a plugin would drift from the other plugin's.
    /// </summary>
    [Fact]
    public void BothBundledPluginUis_CompileTheSharedHelpersAndKeepNoCopy()
    {
        var targets = File.ReadAllText(Path.Combine(RepoRoot(), "plugins", "Directory.Build.targets"));
        Assert.Contains(@"shared\ui\*.cs", targets);
        Assert.Contains("'$(UsesPluginUiHelpers)' == 'true'", targets);
        Assert.Contains("<Using Include=\"SumaFile.Plugins.Ui\" />", targets);

        foreach (var (name, project) in Plugins)
        {
            var ui = Path.Combine(RepoRoot(), "plugins", name, "ui");
            var csproj = File.ReadAllText(Path.Combine(ui, project, project + ".csproj"));
            Assert.Contains("<UsesPluginUiHelpers>true</UsesPluginUiHelpers>", csproj);
            Assert.False(File.Exists(Path.Combine(ui, project, "Views", "Xaml.cs")), $"{project} has its own Xaml.cs.");
            Assert.False(File.Exists(Path.Combine(ui, project, "Views", "ListRows.cs")), $"{project} has its own ListRows.cs.");
            // One set of build files for the bundled plugins, in plugins\.
            Assert.False(File.Exists(Path.Combine(ui, "Directory.Build.props")), $"{name} has its own Directory.Build.props.");
            Assert.False(File.Exists(Path.Combine(ui, "Directory.Build.targets")), $"{name} has its own Directory.Build.targets.");
        }
    }

    /// <summary>
    /// A plugin's version is in its manifest, and its assemblies are built with that version, not with the app's.
    /// </summary>
    [Fact]
    public void ABundledPluginsAssemblies_CarryTheVersionOfItsManifest()
    {
        Assert.True(GitBackendHarness.IsStaged, GitBackendHarness.NotStagedMessage);
        (string Folder, string[] Assemblies)[] staged =
        [
            (GitBackendHarness.PluginFolder, ["SumaFile.Git.dll", "SumaFile.Git.Model.dll"]),
            (RemoteBackendHarness.PluginFolder, ["SumaFile.Remote.dll", "SumaFile.Remote.Model.dll"]),
        ];
        foreach (var (folder, assemblies) in staged)
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "plugin.json")));
            var version = Version.Parse(manifest.RootElement.GetProperty("version").GetString()!);
            foreach (var assembly in assemblies)
            {
                var built = AssemblyName.GetAssemblyName(Path.Combine(folder, assembly)).Version!;
                Assert.Equal(
                    new Version(version.Major, version.Minor, Math.Max(version.Build, 0)),
                    new Version(built.Major, built.Minor, built.Build));
            }
        }
    }

    private static string SharedUiRoot() => Path.Combine(RepoRoot(), "plugins", "shared", "ui");

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cargo.toml"))
                && Directory.Exists(Path.Combine(directory.FullName, "plugins", "shared")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("No folder above " + AppContext.BaseDirectory + " holds Cargo.toml and plugins/shared.");
    }
}
