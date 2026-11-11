using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace SimpleFile.Tests.Git;

/// <summary>
/// Source-shape checks of the Git plugin's two UI projects. The 19041 assembly cannot run outside the real host, so
/// these pin what its run time depends on: no compiled XAML or bindings (spec §5.3), well-formed theme-aware
/// fragments, the automation ids the UI smoke drives, the dialogs' default button and XamlRoot, the four SDK
/// contracts, a WinUI-free decoration path, and the plugin projects' dependency rule (spec §12).
/// </summary>
public class GitPluginSourceShapeTests
{
    /// <summary>The exact automation ids the UI smoke drives (plan Task 13), by constant name.</summary>
    private static readonly Dictionary<string, string> ExpectedAutomationIds = new(StringComparer.Ordinal)
    {
        ["Workbench"] = "Git.Workbench",
        ["Branch"] = "Git.Branch",
        ["Summary"] = "Git.Summary",
        ["Refresh"] = "Git.Refresh",
        ["Fetch"] = "Git.Fetch",
        ["Pull"] = "Git.Pull",
        ["Push"] = "Git.Push",
        ["Commit"] = "Git.Commit",
        ["Stage"] = "Git.Stage",
        ["Unstage"] = "Git.Unstage",
        ["Discard"] = "Git.Discard",
        ["Diff"] = "Git.Diff",
        ["Changes"] = "Git.Changes",
        ["DiffTitle"] = "Git.DiffTitle",
        ["DiffText"] = "Git.DiffText",
        ["DiffSplitter"] = "Git.DiffSplitter",
        ["CommitMessage"] = "Git.CommitMessage",
    };

    [Fact]
    public void GitPlugin_HasNoCompiledXamlOrBindings()
    {
        var ui = UiRoot();
        Assert.True(File.Exists(Path.Combine(ui, "SumaFile.Git", "SumaFile.Git.csproj")), "SumaFile.Git.csproj is missing.");
        Assert.True(File.Exists(Path.Combine(ui, "SumaFile.Git.Model", "SumaFile.Git.Model.csproj")), "SumaFile.Git.Model.csproj is missing.");

        Assert.Empty(SourceFiles(ui, "*.xaml"));
        foreach (var file in SourceFiles(ui, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.False(text.Contains("{Binding", StringComparison.Ordinal), $"{file} uses {{Binding}}.");
            Assert.False(text.Contains("x:Bind", StringComparison.Ordinal), $"{file} uses x:Bind.");
            Assert.False(text.Contains("InitializeComponent", StringComparison.Ordinal), $"{file} calls InitializeComponent.");
            Assert.False(text.Contains("{StaticResource", StringComparison.Ordinal), $"{file} uses {{StaticResource}}; fragments use {{ThemeResource}}.");

            // Plugin UI runs in SumaFile's process and never resolves a temp or user folder itself.
            Assert.False(text.Contains("GetTempPath", StringComparison.Ordinal), $"{file} resolves the temp folder.");
            Assert.False(text.Contains("GetTempFileName", StringComparison.Ordinal), $"{file} creates a temp file.");
            Assert.False(text.Contains("SpecialFolder", StringComparison.Ordinal), $"{file} resolves a user folder.");

            // The App's dialog gate and window chrome are the host's; the plugin reaches them through the SDK.
            Assert.False(text.Contains("DialogHost", StringComparison.Ordinal), $"{file} uses the App's DialogHost.");
            Assert.False(text.Contains(".ShowAsync()", StringComparison.Ordinal), $"{file} shows a dialog around the host gate.");
        }

        foreach (var project in SourceFiles(ui, "*.csproj"))
        {
            Assert.DoesNotContain("UseWinUI", File.ReadAllText(project));
        }
    }

    /// <summary>
    /// The XamlReader fragments only parse at run time in the real host, so check here that each is well-formed XML
    /// and that every name the code looks up exists.
    /// </summary>
    [Fact]
    public void GitPlugin_XamlFragmentsAreWellFormed()
    {
        var fragments = 0;
        foreach (var file in SourceFiles(PluginRoot(), "*.cs"))
        {
            var text = File.ReadAllText(file);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in Regex.Matches(text, "\"\"\"\\r?\\n(?<body>.*?)\\r?\\n[ \\t]*\"\"\"", RegexOptions.Singleline))
            {
                var body = match.Groups["body"].Value.Trim();
                if (!body.StartsWith('<'))
                {
                    continue;
                }

                fragments++;
                var nameEnd = body.IndexOfAny([' ', '\r', '\n', '\t', '>', '/'], 1);
                var xml = body[..nameEnd]
                    + " xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\""
                    + body[nameEnd..];
                var document = XDocument.Parse(xml);
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                foreach (var name in document.Descendants().Select(e => (string?)e.Attribute(x + "Name")).OfType<string>())
                {
                    names.Add(name);
                }
            }

            foreach (Match lookup in Regex.Matches(text, @"Xaml\.Find<\w+>\([^,]+, ""(\w+)"""))
            {
                Assert.True(names.Contains(lookup.Groups[1].Value), $"{Path.GetFileName(file)} looks up {lookup.Groups[1].Value}, which no fragment in it names.");
            }
        }

        Assert.True(fragments >= 3, $"Expected the workbench, its row template and the discard button style; found {fragments}.");
    }

    [Fact]
    public void GitPlugin_ProjectsReferenceOnlyTheSdkAndEachOther()
    {
        string[] allowedPackages = ["Microsoft.WindowsAppSDK", "Microsoft.Windows.SDK.BuildTools", "CommunityToolkit.Mvvm"];
        var projects = new[]
        {
            Path.Combine(UiRoot(), "SumaFile.Git", "SumaFile.Git.csproj"),
            Path.Combine(UiRoot(), "SumaFile.Git.Model", "SumaFile.Git.Model.csproj"),
        };

        foreach (var path in projects)
        {
            var text = File.ReadAllText(path);
            foreach (var forbidden in new[] { "SimpleFile.Core", "SimpleFile.Ipc", "SimpleFile.PluginHost", "SimpleFile.App" })
            {
                Assert.False(text.Contains(forbidden, StringComparison.Ordinal), $"{Path.GetFileName(path)} references {forbidden}.");
            }

            var project = XDocument.Parse(text);
            foreach (var reference in project.Descendants("ProjectReference"))
            {
                var include = (string?)reference.Attribute("Include") ?? "";
                var name = Path.GetFileNameWithoutExtension(include.Replace('\\', '/'));
                Assert.Contains(name, new[] { "SimpleFile.PluginSdk", "SumaFile.Git.Model" });
                if (name == "SimpleFile.PluginSdk")
                {
                    Assert.Equal("false", ValueOf(reference, "Private"));
                    Assert.Equal("runtime", ValueOf(reference, "ExcludeAssets"));
                }
            }

            foreach (var package in project.Descendants("PackageReference"))
            {
                var include = (string?)package.Attribute("Include") ?? "";
                Assert.Contains(include, allowedPackages);
                Assert.True(ValueOf(package, "ExcludeAssets") == "runtime", $"{include} in {Path.GetFileName(path)} is not compile-only.");
                Assert.True(ValueOf(package, "PrivateAssets") == "all", $"{include} in {Path.GetFileName(path)} flows to dependents.");
            }
        }

        // The UI assembly targets 19041 and builds on the model; the model is WinUI-free and takes no package at all.
        var ui = XDocument.Load(projects[0]);
        Assert.Equal("net10.0-windows10.0.19041.0", ui.Descendants("TargetFramework").Single().Value);
        Assert.Equal("10.0.19041.0", ui.Descendants("TargetPlatformMinVersion").Single().Value);
        Assert.Equal("true", ui.Descendants("EnableDynamicLoading").Single().Value);
        Assert.Contains(
            ui.Descendants("ProjectReference"),
            r => ((string?)r.Attribute("Include") ?? "").EndsWith("SumaFile.Git.Model.csproj", StringComparison.Ordinal));
        Assert.Equal(
            ["Microsoft.Windows.SDK.BuildTools", "Microsoft.WindowsAppSDK"],
            ui.Descendants("PackageReference").Select(p => (string?)p.Attribute("Include") ?? "").Order(StringComparer.Ordinal));

        var model = XDocument.Load(projects[1]);
        Assert.Equal("net10.0-windows", model.Descendants("TargetFramework").Single().Value);
        Assert.Empty(model.Descendants("PackageReference"));
    }

    [Fact]
    public void GitAutomationIds_AreExactly()
    {
        var idsFile = Path.Combine(ViewsRoot(), "GitAutomationIds.cs");
        var declared = Regex.Matches(File.ReadAllText(idsFile), @"public const string (\w+) = ""([^""]+)"";")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

        Assert.Equal(ExpectedAutomationIds.OrderBy(p => p.Key, StringComparer.Ordinal), declared.OrderBy(p => p.Key, StringComparer.Ordinal));

        var users = string.Join(
            "\n",
            SourceFiles(PluginRoot(), "*.cs")
                .Where(file => !string.Equals(Path.GetFileName(file), "GitAutomationIds.cs", StringComparison.Ordinal))
                .Select(File.ReadAllText));
        foreach (var name in declared.Keys)
        {
            Assert.True(
                Regex.IsMatch(users, @"\bGitAutomationIds\." + name + @"\b"),
                $"GitAutomationIds.{name} ({declared[name]}) is never used.");
        }

        // The automation names 1.0.4's workbench had, which screen readers and the smoke still find.
        var views = AllViews();
        foreach (var automationName in new[] { "Refresh Git status", "Fetch", "Discard selected changes", "Git changes", "Resize Git diff preview" })
        {
            Assert.True(
                views.Contains($"AutomationProperties.Name=\"{automationName}\"", StringComparison.Ordinal)
                || views.Contains($"AutomationProperties.SetName(this, \"{automationName}\")", StringComparison.Ordinal),
                $"No element is named '{automationName}'.");
        }
    }

    [Fact]
    public void GitWorkbenchPanel_ListVirtualizesAndTheSplitterClampsTheDiff()
    {
        var panel = File.ReadAllText(Path.Combine(ViewsRoot(), "GitWorkbenchPanel.cs"));
        Assert.Contains("SelectionMode=\"Extended\"", panel);
        Assert.Contains("ListRows.Attach<GitChangeRow>(", panel);
        Assert.Equal(2, Regex.Matches(panel, @"Xaml\.Load").Count);
        Assert.Contains(".ItemsSource = ", panel);
        Assert.False(Regex.IsMatch(panel, @"\.Items\.(Add|Insert|Clear)\("), "The panel fills ListView.Items directly.");
        Assert.DoesNotContain("new ListViewItem", panel);
        Assert.Contains("FontFamily=\"Consolas\"", panel);
        Assert.Contains("IsReadOnly=\"True\"", panel);
        Assert.Contains("_model.PropertyChanged += ", panel);
        Assert.Contains("_model.PropertyChanged -= ", panel);
        Assert.Contains("Foreground=\"{ThemeResource SystemFillColorCriticalBrush}\"", Slice(panel, "x:Name=\"DiscardButton\"", "</Button>"));
        var diff = panel.IndexOf("x:Name=\"DiffButton\"", StringComparison.Ordinal);
        var discard = panel.IndexOf("x:Name=\"DiscardButton\"", StringComparison.Ordinal);
        Assert.True(diff > 0 && discard > diff, "Discard sits after the other actions.");

        var splitter = File.ReadAllText(Path.Combine(ViewsRoot(), "GitDiffSplitter.cs"));
        Assert.Matches(@"class GitDiffSplitter\s*:\s*Grid\b", splitter);
        Assert.Contains("ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast)", splitter);
        Assert.Contains("MinDiffWidth = 260", splitter);
        Assert.Contains("MaxDiffWidth = 960", splitter);
        Assert.Contains("DefaultDiffWidth = 420", splitter);
        Assert.Contains("DoubleTapped += ", splitter);
        Assert.Contains("new GridLength(DefaultDiffWidth)", splitter);
    }

    [Fact]
    public void GitPrompts_DiscardDefaultsToCancel()
    {
        var prompts = File.ReadAllText(Path.Combine(ViewsRoot(), "GitPrompts.cs"));
        var discard = Slice(prompts, "public async Task<bool> ConfirmDiscardAsync(", "== PluginDialogResult.Primary;");

        Assert.Contains("PrimaryButtonText = \"Discard\"", discard);
        Assert.Contains("CloseButtonText = \"Cancel\"", discard);
        Assert.Contains("DefaultButton = ContentDialogButton.Close", discard);
        Assert.Contains("PrimaryButtonStyle = Xaml.Load<Style>(CriticalPrimaryButtonStyle)", discard);
        Assert.Contains("GitText.DiscardConfirmationText(rows)", discard);
        Assert.Contains("_host.Dialogs.ShowDialogAsync(dialog, ct)", discard);
        Assert.Contains("\"{ThemeResource SystemFillColorCriticalBrush}\"", Slice(prompts, "CriticalPrimaryButtonStyle = \"\"\"", "\"\"\";"));

        var commit = Slice(prompts, "public async Task<string?> PromptCommitMessageAsync(", "public async Task<bool> ConfirmDiscardAsync(");
        Assert.Contains("GitAutomationIds.CommitMessage", commit);
        Assert.Contains("CloseButtonText = \"Cancel\"", commit);
        Assert.Contains("result == PluginDialogResult.Primary ? input.Text : null", commit);
    }

    [Fact]
    public void GitPrompts_UseTheShownPanelsXamlRoot()
    {
        var prompts = File.ReadAllText(Path.Combine(ViewsRoot(), "GitPrompts.cs"));
        Assert.Equal(2, Regex.Matches(prompts, @"XamlRoot = _shownPanelXamlRoot\(\),").Count);
        Assert.Equal(2, Regex.Matches(prompts, @"new ContentDialog\b").Count);
        Assert.DoesNotContain("Content.XamlRoot", prompts);

        var plugin = File.ReadAllText(Path.Combine(PluginRoot(), "GitPlugin.cs"));
        Assert.Contains("new GitPrompts(host, ShownPanelXamlRoot)", plugin);
        var root = Slice(plugin, "private XamlRoot? ShownPanelXamlRoot()", "private (GitWorkbenchController Controller");
        Assert.Contains("host.Panels.IsVisible(GitWorkbenchController.WorkbenchPanelId)", root);
        Assert.Contains("_shownPanel.TryGetTarget(out var element)", root);
        Assert.Contains("element.IsLoaded ? element.XamlRoot : null", root);
        Assert.Contains("return null;", root);

        // Every presentation's body becomes the shown panel.
        var create = Slice(plugin, "public object CreatePanel(PluginPanelRequest request)", "public Task<PluginFolderDecorations> GetDecorationsAsync(");
        Assert.Contains("new GitWorkbenchPanel(controller, model, request.Detached)", create);
        Assert.Contains("_shownPanel = new WeakReference<FrameworkElement>(panel.Root)", create);
        Assert.Contains("controller.RefreshAsync(silent: true, CancellationToken.None)", create);
        Assert.Contains("return panel.Root;", create);
    }

    [Fact]
    public void GitPlugin_ImplementsTheFourContracts()
    {
        var plugin = File.ReadAllText(Path.Combine(PluginRoot(), "GitPlugin.cs"));

        Assert.Contains("namespace SumaFile.Git;", plugin);
        Assert.Matches(@"public sealed class GitPlugin\s*:\s*IPlugin, ICommandHandler, IPanelProvider, IRowDecorationProvider\b", plugin);

        var activate = Slice(plugin, "public Task ActivateAsync(IPluginHost host, CancellationToken ct)", "public async Task ExecuteAsync(");
        Assert.Contains("new GitBackendClient(host)", activate);
        Assert.Contains("new GitDecorationProvider(backend, host.Settings, host.Panels, host.Logger)", activate);
        Assert.Contains("host.Workspace.Changed += _workspaceChanged;", activate);
        Assert.Contains("controller.OnWorkspaceChanged()", activate);

        var execute = Slice(plugin, "public async Task ExecuteAsync(", "public object CreatePanel(");
        Assert.Contains("await controller.ExecuteAsync(context, ct);", execute);

        var deactivate = Slice(plugin, "public async Task DeactivateAsync()", "private XamlRoot? ShownPanelXamlRoot()");
        Assert.Contains("host.Workspace.Changed -= handler;", deactivate);
        Assert.Contains("await controller.StopAsync(StopTimeout)", deactivate);
        Assert.Contains("StopTimeout = TimeSpan.FromSeconds(5)", plugin);

        // git-diff-selected shows the workbench through the host before it previews the diff.
        var controller = File.ReadAllText(Path.Combine(ModelRoot(), "GitWorkbenchController.cs"));
        var diff = Slice(controller, "private async Task DiffSelectedAsync(", "private async Task RunCommandAsync(");
        var show = diff.IndexOf("_host.Panels.ShowAsync(WorkbenchPanelId, ct)", StringComparison.Ordinal);
        var preview = diff.IndexOf("ShowDiffCoreAsync(paths", StringComparison.Ordinal);
        Assert.True(show > 0 && preview > show, "The diff command shows the workbench, then the diff.");
    }

    [Fact]
    public void GitPlugin_DecorationsTouchNoWinUi()
    {
        var plugin = File.ReadAllText(Path.Combine(PluginRoot(), "GitPlugin.cs"));
        var decorations = Slice(plugin, "public Task<PluginFolderDecorations> GetDecorationsAsync(", "public async Task DeactivateAsync()").Trim();

        Assert.Equal(
            "public Task<PluginFolderDecorations> GetDecorationsAsync(PluginDecorationRequest request, CancellationToken ct) =>\n"
            + "        _decorations is { } decorations ? decorations.GetAsync(request, ct) : Task.FromResult(PluginFolderDecorations.Empty);",
            decorations.Replace("\r\n", "\n", StringComparison.Ordinal));

        // The provider and everything it reaches live in the WinUI-free model.
        foreach (var file in SourceFiles(ModelRoot(), "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.False(text.Contains("Microsoft.UI", StringComparison.Ordinal), $"{Path.GetFileName(file)} touches WinUI.");
            Assert.False(text.Contains("Windows.", StringComparison.Ordinal) && text.Contains("using Windows", StringComparison.Ordinal), $"{Path.GetFileName(file)} uses WinRT.");
        }
    }

    /// <summary>The text from <paramref name="start"/> up to (not including) <paramref name="end"/>.</summary>
    private static string Slice(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' was not found.");
        var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"'{end}' was not found after '{start}'.");
        return text[from..to];
    }

    /// <summary>An attribute or child element value, the two ways MSBuild items carry metadata.</summary>
    private static string? ValueOf(XElement item, string name) =>
        (string?)item.Attribute(name) ?? item.Element(name)?.Value;

    private static string AllViews() => string.Join("\n", SourceFiles(ViewsRoot(), "*.cs").Select(File.ReadAllText));

    /// <summary>Source files under <paramref name="root"/>, without build output.</summary>
    private static IReadOnlyList<string> SourceFiles(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(file =>
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                return !relative.Split('/').Any(part => part is "bin" or "obj" or ".mono");
            })
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();

    private static string UiRoot() => Path.Combine(RepoRoot(), "plugins", "git", "ui");

    private static string PluginRoot() => Path.Combine(UiRoot(), "SumaFile.Git");

    private static string ModelRoot() => Path.Combine(UiRoot(), "SumaFile.Git.Model");

    private static string ViewsRoot() => Path.Combine(PluginRoot(), "Views");

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cargo.toml"))
                && Directory.Exists(Path.Combine(directory.FullName, "plugins", "git", "ui")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("No folder above " + AppContext.BaseDirectory + " holds Cargo.toml and plugins/git/ui.");
    }
}
