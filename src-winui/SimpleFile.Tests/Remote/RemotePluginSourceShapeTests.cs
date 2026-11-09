using System.Text.RegularExpressions;
using System.Xml.Linq;
using SumaFile.Remote.Model;
using Xunit;

namespace SimpleFile.Tests.Remote;

/// <summary>
/// Source-shape checks of the remote plugin's UI assembly (spec §5.3: no compiled XAML from a plugin folder, no
/// bindings), its use of the host's window and dialog services, the plain-FTP consent and host-key wording carried
/// over from the 1.0.4 manager, and the plugin projects' dependency rule (spec §12).
/// </summary>
public class RemotePluginSourceShapeTests
{
    /// <summary>The exact automation ids the UI smoke drives (plan Task 11), by constant name.</summary>
    private static readonly Dictionary<string, string> ExpectedAutomationIds = new(StringComparer.Ordinal)
    {
        ["ProfileList"] = "Remote.ProfileList",
        ["NewProfile"] = "Remote.NewProfile",
        ["EditProfile"] = "Remote.EditProfile",
        ["DeleteProfile"] = "Remote.DeleteProfile",
        ["Connect"] = "Remote.Connect",
        ["Disconnect"] = "Remote.Disconnect",
        ["Refresh"] = "Remote.Refresh",
        ["LocalPath"] = "Remote.LocalPath",
        ["LocalGo"] = "Remote.LocalGo",
        ["LocalList"] = "Remote.LocalList",
        ["RemotePath"] = "Remote.RemotePath",
        ["RemoteList"] = "Remote.RemoteList",
        ["Upload"] = "Remote.Upload",
        ["Download"] = "Remote.Download",
        ["NewFolder"] = "Remote.NewFolder",
        ["Rename"] = "Remote.Rename",
        ["DeleteEntries"] = "Remote.DeleteEntries",
        ["TransferList"] = "Remote.TransferList",
        ["CancelTransfer"] = "Remote.CancelTransfer",
        ["RetryTransfer"] = "Remote.RetryTransfer",
        ["TransferOptions"] = "Remote.TransferOptions",
        ["Conflict"] = "Remote.Conflict",
        ["Status"] = "Remote.Status",
        ["ProfileName"] = "RemoteProfile.Name",
        ["ProfileProtocol"] = "RemoteProfile.Protocol",
        ["ProfileHost"] = "RemoteProfile.Host",
        ["ProfilePort"] = "RemoteProfile.Port",
        ["ProfileUser"] = "RemoteProfile.User",
        ["ProfileRootPath"] = "RemoteProfile.RootPath",
        ["ProfileAuth"] = "RemoteProfile.Auth",
        ["ProfileSecret"] = "RemoteProfile.Secret",
        ["ProfileSaveSecret"] = "RemoteProfile.SaveSecret",
        ["ProfileKeyPath"] = "RemoteProfile.KeyPath",
        ["ProfilePassiveMode"] = "RemoteProfile.PassiveMode",
        ["ProfileAllowPlainFtp"] = "RemoteProfile.AllowPlainFtp",
        ["HostKeyFingerprint"] = "RemoteHostKey.Fingerprint",
        ["NamePrompt"] = "Remote.NamePrompt",
        ["SidebarList"] = "Remote.SidebarList",
        ["SidebarEmpty"] = "Remote.SidebarEmpty",
        ["SettingsOpenManager"] = "Remote.SettingsOpenManager",
    };

    [Fact]
    public void RemoteUi_HasNoCompiledXamlOrBindings()
    {
        var ui = UiRoot();
        Assert.True(File.Exists(Path.Combine(ui, "SumaFile.Remote", "SumaFile.Remote.csproj")), "SumaFile.Remote.csproj is missing.");

        Assert.Empty(SourceFiles(ui, "*.xaml"));
        foreach (var file in SourceFiles(ui, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.False(text.Contains("{Binding", StringComparison.Ordinal), $"{file} uses {{Binding}}.");
            Assert.False(text.Contains("x:Bind", StringComparison.Ordinal), $"{file} uses x:Bind.");
            Assert.False(text.Contains("InitializeComponent", StringComparison.Ordinal), $"{file} calls InitializeComponent.");
        }

        foreach (var project in SourceFiles(ui, "*.csproj"))
        {
            Assert.DoesNotContain("UseWinUI", File.ReadAllText(project));
        }
    }

    /// <summary>
    /// The XamlReader fragments only parse at run time in the real host, so check here that each is well-formed XML,
    /// uses <c>{ThemeResource}</c> rather than <c>{StaticResource}</c>, and that every name the code looks up exists.
    /// </summary>
    [Fact]
    public void RemoteUi_XamlFragmentsAreWellFormed()
    {
        var fragments = 0;
        foreach (var file in SourceFiles(ViewsRoot(), "*.cs"))
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
                Assert.DoesNotContain("{StaticResource", body);
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

        Assert.True(fragments >= 5, $"Expected the window, its item and the sidebar row fragments; found {fragments}.");
    }

    [Fact]
    public void RemoteUi_UsesEveryAutomationId()
    {
        var idsFile = Path.Combine(ViewsRoot(), "RemoteAutomationIds.cs");
        var declared = Regex.Matches(File.ReadAllText(idsFile), @"public const string (\w+) = ""([^""]+)"";")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

        Assert.Equal(ExpectedAutomationIds.OrderBy(p => p.Key, StringComparer.Ordinal), declared.OrderBy(p => p.Key, StringComparer.Ordinal));

        var users = string.Join(
            "\n",
            SourceFiles(PluginRoot(), "*.cs")
                .Where(file => !string.Equals(Path.GetFileName(file), "RemoteAutomationIds.cs", StringComparison.Ordinal))
                .Select(File.ReadAllText));
        foreach (var name in declared.Keys)
        {
            Assert.True(
                Regex.IsMatch(users, @"\bRemoteAutomationIds\." + name + @"\b"),
                $"RemoteAutomationIds.{name} ({declared[name]}) is never used.");
        }
    }

    [Fact]
    public void RemoteUi_UsesTheHostWindowAndDialogServices()
    {
        var window = File.ReadAllText(Path.Combine(ViewsRoot(), "RemoteManagerWindow.cs"));

        Assert.Contains("Windows.Attach(", window);
        Assert.Contains("new PluginWindowOptions(\"FTP/SFTP manager\", 1180, 680)", window);
        Assert.Contains("WinRT.Interop.WindowNative.GetWindowHandle(this)", window);
        Assert.Matches(@"class RemoteManagerWindow\s*:\s*Window\b", window);

        foreach (var file in SourceFiles(PluginRoot(), "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.False(text.Contains("ContentDialog.ShowAsync", StringComparison.Ordinal), $"{file} shows a dialog around the host gate.");
            Assert.False(text.Contains(".ShowAsync()", StringComparison.Ordinal), $"{file} shows a dialog around the host gate.");
            Assert.False(text.Contains("DialogHost", StringComparison.Ordinal), $"{file} uses the App's DialogHost.");
            Assert.False(text.Contains("SecondaryWindowChrome", StringComparison.Ordinal), $"{file} uses the App's window chrome.");
        }

        var views = AllViews();
        Assert.Contains("Dialogs.ShowDialogAsync(", views);
    }

    [Fact]
    public void RemoteUi_AsksForPlainFtpConsentAndTrustsHostKeysExplicitly()
    {
        var views = AllViews();

        Assert.Contains("\"Connect without encryption. Passwords and files are sent in plain text.\"", views);
        Assert.Contains("\"Confirm the unencrypted connection to save this profile\"", views);
        Assert.Contains("IsPrimaryButtonEnabled = !needsConsent", views);
        Assert.Contains("DefaultPortFor(", views);
        Assert.DoesNotContain("Allow plain FTP", views);

        var profileDialog = File.ReadAllText(Path.Combine(ViewsRoot(), "RemoteProfileDialog.cs"));
        var connection = UiAuditSprint3Tests.Slice(profileDialog, "\"Connection\",", "rootBox),");
        var protocolAt = connection.IndexOf("protocolBox,", StringComparison.Ordinal);
        var consentAt = connection.IndexOf("insecureBox,", StringComparison.Ordinal);
        Assert.True(protocolAt >= 0 && consentAt > protocolAt, "Plain-FTP consent sits directly under Protocol.");

        Assert.Contains("Title = \"Trust this server?\"", views);
        Assert.Contains("PrimaryButtonText = \"Trust and connect\"", views);
        Assert.Contains("Title = \"Server key changed\"", views);
        Assert.Contains("PrimaryButtonText = \"Replace trusted key\"", views);
        Assert.Contains("TrustFingerprintAndConnectAsync(fingerprint, token)", views);
        var hostKeyFile = SourceFiles(ViewsRoot(), "*.cs")
            .Select(File.ReadAllText)
            .Single(text => text.Contains("Title = \"Server key changed\"", StringComparison.Ordinal));
        var mismatch = UiAuditSprint3Tests.Slice(hostKeyFile, "Title = \"Server key changed\"", "ShowDialogAsync(");
        Assert.Contains("DefaultButton = ContentDialogButton.Close", mismatch);

        Assert.Contains("\"Insecure\"", views);
        Assert.Contains("\"Transfer options...\"", views);
        Assert.Contains("\"Replace (keeps a backup until the transfer finishes)\"", views);
        Assert.Contains(".IsInsecure", File.ReadAllText(Path.Combine(ViewsRoot(), "RemoteManagerWindow.Lists.cs")));
        Assert.Contains(".IsInsecure", File.ReadAllText(Path.Combine(ViewsRoot(), "RemoteSidebarSection.cs")));
        Assert.DoesNotContain("\"Reload\"", views);
    }

    /// <summary>
    /// Plugin windows share the host's UI thread, so a folder of tens of thousands of entries must not build one
    /// container and one <c>XamlReader</c> parse per entry: the lists bind a coalesced snapshot as
    /// <c>ItemsSource</c>, parse each row template once, and fill the realized (recycled) containers in
    /// <c>ContainerContentChanging</c>.
    /// </summary>
    [Fact]
    public void RemoteUi_ListsVirtualizeAndParseEachRowTemplateOnce()
    {
        var files = new[]
        {
            // The local and remote panes share the entry template.
            (Name: "RemoteManagerWindow.Lists.cs", Templates: 3, Lists: 4),
            (Name: "RemoteSidebarSection.cs", Templates: 1, Lists: 1),
        };

        foreach (var (name, templates, lists) in files)
        {
            var text = File.ReadAllText(Path.Combine(ViewsRoot(), name));
            Assert.False(text.Contains("new ListViewItem", StringComparison.Ordinal), $"{name} builds its own item containers, which bypasses virtualization.");
            Assert.False(Regex.IsMatch(text, @"\.Items\.(Add|Insert|Clear)\("), $"{name} fills ListView.Items directly.");
            Assert.Contains(".ItemsSource = ", text);
            Assert.Equal(lists, Regex.Matches(text, @"ListRows\.Attach<").Count);
            Assert.Equal(templates, Regex.Matches(text, @"Xaml\.Load<DataTemplate>\(").Count);
            Assert.Equal(templates, Regex.Matches(text, @"Xaml\.Load").Count);
        }

        var rows = File.ReadAllText(Path.Combine(ViewsRoot(), "ListRows.cs"));
        Assert.Contains(".ItemTemplate = template", rows);
        Assert.Contains("ContainerContentChanging += ", rows);
        Assert.Contains("ContentTemplateRoot", rows);
        Assert.Contains("InRecycleQueue", rows);

        var manager = File.ReadAllText(Path.Combine(ViewsRoot(), "RemoteManagerWindow.Lists.cs"));
        Assert.Contains("new CoalescingSnapshot<", manager);
        Assert.DoesNotContain("Items.IndexOf(", manager);
    }

    /// <summary>
    /// <c>ItemClick</c> reports the data item for an <c>ItemsSource</c> list; the shared resolver also maps a
    /// container, or a container's content, back to its item, so no handler depends on which one WinUI passes.
    /// </summary>
    [Fact]
    public void RemoteUi_ItemClickHandlersResolveTheClickedRow()
    {
        var rows = File.ReadAllText(Path.Combine(ViewsRoot(), "ListRows.cs"));
        Assert.Contains("ItemFromContainer(", rows);
        Assert.Contains("ContainerFromItem(", rows);
        Assert.Contains("ContentTemplateRoot", rows);

        var views = AllViews();
        Assert.DoesNotContain("ClickedItem is ListViewItem", views);
        var handlers = Regex.Matches(views, @"ItemClick \+= (\w+);").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(3, handlers.Length);
        foreach (var handler in handlers)
        {
            var body = Regex.Match(views, @"void " + handler + @"\(object sender, ItemClickEventArgs e\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline);
            Assert.True(body.Success, $"{handler} is not declared.");
            Assert.Contains("ListRows.Clicked<", body.Groups["body"].Value);
        }
    }

    /// <summary>
    /// The settings page button, the sidebar rows and the command open the manager from WinUI event handlers, where
    /// an escaping exception ends the whole App. They go through the reporting <c>SingleWindowSlot.TryShow</c>.
    /// </summary>
    [Fact]
    public void RemotePlugin_ManagerEntryPointsReportFailuresInsteadOfThrowing()
    {
        var plugin = File.ReadAllText(Path.Combine(PluginRoot(), "RemotePlugin.cs"));
        Assert.Contains("SingleWindowSlot<RemoteManagerWindow> _manager = new(", plugin);
        Assert.Contains(".TryShow(", plugin);
        Assert.Contains("Logger.Error(", plugin);
        Assert.Contains("Dialogs.ShowMessageAsync(", plugin);
        Assert.DoesNotContain("ShowManager(", plugin);
        Assert.DoesNotContain("_window = window", plugin);

        foreach (var name in new[] { "RemoteSettingsPage.cs", "RemoteSidebarSection.cs" })
        {
            var text = File.ReadAllText(Path.Combine(ViewsRoot(), name));
            Assert.Contains(".OpenManager(", text);
            Assert.DoesNotContain("ShowManager(", text);
        }
    }

    [Fact]
    public void RemoteBackend_HostKeyMessagesKeepTheViewModelPrefixes()
    {
        var sftp = File.ReadAllText(Path.Combine(RepoRoot(), "plugins", "remote", "backend", "src", "sftp.rs"));

        Assert.Contains("\"" + RemoteManagerViewModel.UntrustedFingerprintPrefix + ".", sftp);
        Assert.Contains("\"" + RemoteManagerViewModel.FingerprintMismatchPrefix + ".", sftp);
        Assert.Contains("Expected {expected}, got {fingerprint}", sftp);
    }

    [Fact]
    public void RemotePlugin_ProjectsReferenceOnlyTheSdkAndEachOther()
    {
        string[] allowedPackages = ["Microsoft.WindowsAppSDK", "Microsoft.Windows.SDK.BuildTools", "CommunityToolkit.Mvvm"];
        var projects = new[]
        {
            Path.Combine(UiRoot(), "SumaFile.Remote", "SumaFile.Remote.csproj"),
            Path.Combine(UiRoot(), "SumaFile.Remote.Model", "SumaFile.Remote.Model.csproj"),
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
                Assert.Contains(name, new[] { "SimpleFile.PluginSdk", "SumaFile.Remote.Model" });
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

        var ui = XDocument.Load(projects[0]);
        Assert.Contains(
            ui.Descendants("ProjectReference"),
            r => ((string?)r.Attribute("Include") ?? "").EndsWith("SumaFile.Remote.Model.csproj", StringComparison.Ordinal));
        Assert.Equal(
            allowedPackages.OrderBy(p => p, StringComparer.Ordinal),
            ui.Descendants("PackageReference").Select(p => (string?)p.Attribute("Include") ?? "").OrderBy(p => p, StringComparer.Ordinal));
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
                return !relative.Split('/').Any(part => part is "bin" or "obj");
            })
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();

    private static string UiRoot() => Path.Combine(RepoRoot(), "plugins", "remote", "ui");

    private static string PluginRoot() => Path.Combine(UiRoot(), "SumaFile.Remote");

    private static string ViewsRoot() => Path.Combine(PluginRoot(), "Views");

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cargo.toml"))
                && Directory.Exists(Path.Combine(directory.FullName, "plugins", "remote")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("No folder above " + AppContext.BaseDirectory + " holds Cargo.toml and plugins/remote.");
    }
}
