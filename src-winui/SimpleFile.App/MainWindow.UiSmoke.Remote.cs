#if DEBUG
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Trust;

namespace SimpleFile.App;

// The bundled FTP/SFTP plugin (com.sumafile.remote) in the real host. Excluded from release builds.
public sealed partial class MainWindow
{
    private const string SmokeRemoteId = "com.sumafile.remote";
    private const string SmokeRemoteManagerCommand = "plugin:" + SmokeRemoteId + ":ftp-sftp-manager";
    private const string SmokeRemoteSectionId = "PluginSection." + SmokeRemoteId + ".connections";
    private const string SmokeRemoteManagerTitle = "FTP/SFTP manager";
    private static readonly TimeSpan SmokeRemoteWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// "with": the first real-host load of <c>SumaFile.Remote.dll</c>. The bundled plugin is trusted through
    /// bundled.json alone, its Connections section loads from the backend (which keeps its data under the smoke's
    /// data root), its command opens one code-built manager window, the profile dialog opens on that window's own
    /// XamlRoot and closes without saving, and closing the window empties the plugin's window host. Every wait is
    /// bounded at 10 s. Other modes check nothing yet.
    /// </summary>
    private async Task SmokeRemoteAsync(List<object> results, string mode)
    {
        if (mode != "with")
        {
            return;
        }

        var manager = _plugins ?? throw new InvalidOperationException("Plugin manager was not created");
        var workspace = _workspace ?? throw new InvalidOperationException("Workspace is not available");

        // 1. Discovered from <exe dir>\plugins and Enabled through bundled.json; the approvals file holds nothing for it.
        var record = manager.Plugins.FirstOrDefault(item => item.Id == SmokeRemoteId);
        SmokeAssert(record is not null, $"Plugin {SmokeRemoteId} was not discovered");
        SmokeAssert(record!.State == PluginState.Enabled, $"Plugin {SmokeRemoteId} is {record.State}, expected Enabled: {record.Error}");
        var installFolder = Path.Combine(PluginDiscovery.InstallRoot(), SmokeRemoteId);
        SmokeAssert(SmokeSameFolder(record.Discovered.Folder, installFolder),
            $"Plugin {SmokeRemoteId} was discovered in {record.Discovered.Folder}, expected {installFolder}");
        var approvalsPath = Environment.GetEnvironmentVariable(PluginApprovalStore.PathVariable);
        SmokeAssert(!string.IsNullOrWhiteSpace(approvalsPath), $"{PluginApprovalStore.PathVariable} is not set");
        SmokeAssert(!File.Exists(approvalsPath) || !File.ReadAllText(approvalsPath!).Contains(SmokeRemoteId, StringComparison.Ordinal),
            $"The approvals file {approvalsPath} holds a record for {SmokeRemoteId}; it must be trusted through bundled.json alone");

        // 2. The Connections section loads its empty list from the backend, which used the disposable data root.
        await SmokeRemoteSectionAsync(workspace);
        var profilesDb = Path.Combine(UiSmokeRoot()!, "plugins-data", SmokeRemoteId, "remote-profiles.db");
        await SmokeRemoteWaitAsync(() => File.Exists(profilesDb), () => $"{profilesDb} to exist");

        // 3. The command opens exactly one manager window; running it again keeps that one window.
        await RunAppCommandAsync(SmokeRemoteManagerCommand);
        Window? window = null;
        await SmokeRemoteWaitAsync(
            () => PluginWindowHostFor(SmokeRemoteId)?.OpenWindows is { Count: 1 } open && (window = open[0]) is not null,
            () => $"one window in {SmokeRemoteId}'s window host after {SmokeRemoteManagerCommand}, found "
                + (PluginWindowHostFor(SmokeRemoteId)?.OpenWindows.Count ?? -1));
        SmokeAssert(window!.Title == SmokeRemoteManagerTitle, $"The manager window is titled '{window.Title}', expected '{SmokeRemoteManagerTitle}'");
        var type = window.GetType();
        SmokeAssert(type.FullName == "SumaFile.Remote.Views.RemoteManagerWindow", $"The manager window is a {type.FullName}");
        var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(type.Assembly);
        SmokeAssert(context is not null && !ReferenceEquals(context, System.Runtime.Loader.AssemblyLoadContext.Default),
            $"RemoteManagerWindow was loaded into the {context?.Name ?? "unknown"} load context, not the plugin's");

        await RunAppCommandAsync(SmokeRemoteManagerCommand);
        var settle = Stopwatch.StartNew();
        while (settle.Elapsed < TimeSpan.FromMilliseconds(750))
        {
            var open = PluginWindowHostFor(SmokeRemoteId)?.OpenWindows;
            SmokeAssert(open is { Count: 1 } && ReferenceEquals(open[0], window),
                $"Running {SmokeRemoteManagerCommand} again left {open?.Count ?? -1} manager windows, expected the same one");
            await Task.Delay(50);
        }

        // 4. New profile opens the profile dialog on the manager window's own XamlRoot; Cancel saves nothing.
        var root = window.Content as FrameworkElement;
        SmokeAssert(root is not null, "The manager window has no FrameworkElement content");
        Button? newProfile = null;
        await SmokeRemoteWaitAsync(
            () => root!.IsLoaded && root.XamlRoot is not null
                && (newProfile = SmokeFindById<Button>(root, "Remote.NewProfile")) is { IsLoaded: true },
            () => "Remote.NewProfile loaded in the manager window");
        var profileList = SmokeFindById<ListView>(root!, "Remote.ProfileList");
        SmokeAssert(profileList is not null, "Remote.ProfileList is missing");
        SmokeAssert(profileList!.Items.Count == 0, $"Remote.ProfileList holds {profileList.Items.Count} profiles before any save");
        SmokeInvoke(newProfile!);
        ContentDialog? dialog = null;
        await SmokeRemoteWaitAsync(
            () => (dialog = SmokeOpenDialog(root!.XamlRoot!)) is { IsLoaded: true }
                && Equals(dialog.Title, "New remote profile")
                && SmokeDescendants<Button>(dialog).Any(button => button.Name == "CloseButton"),
            () => "the 'New remote profile' dialog on the manager window, found "
                + (SmokeOpenDialog(root!.XamlRoot!)?.Title ?? "none"));
        SmokeAssert(Content?.XamlRoot is not { } mainRoot
            || !VisualTreeHelper.GetOpenPopupsForXamlRoot(mainRoot).Any(popup => ReferenceEquals(popup.Child, dialog)),
            "The profile dialog opened on the main window");
        SmokeAssert(SmokeFindById<TextBox>(dialog!, "RemoteProfile.Name") is not null, "The profile dialog has no RemoteProfile.Name box");
        SmokeCloseDialog(dialog!);
        await SmokeRemoteWaitAsync(() => SmokeOpenDialog(root!.XamlRoot!) is null, () => "the profile dialog to close");
        await Task.Delay(250);
        SmokeAssert(profileList.Items.Count == 0, $"Remote.ProfileList holds {profileList.Items.Count} profiles after Cancel");

        // 5. Closing the manager window empties the window host.
        window.Close();
        await SmokeRemoteWaitAsync(
            () => PluginWindowHostFor(SmokeRemoteId)?.OpenWindows.Count == 0,
            () => $"{SmokeRemoteId}'s window host to empty after the close, holds "
                + (PluginWindowHostFor(SmokeRemoteId)?.OpenWindows.Count ?? -1));

        record = manager.Plugins.FirstOrDefault(item => item.Id == SmokeRemoteId);
        SmokeAssert(record?.State == PluginState.Enabled, $"Plugin {SmokeRemoteId} is {record?.State} after the load checks: {record?.Error}");
        results.Add(new { scenario = "remote", mode, stage = "load", passed = true });
    }

    /// <summary>
    /// The Connections section: one section for the plugin, headed "Connections", whose body shows
    /// <c>Remote.SidebarEmpty</c> once the sidebar is shown. Leaves the sidebar as it found it.
    /// </summary>
    private async Task SmokeRemoteSectionAsync(ExplorerWorkspace workspace)
    {
        var views = _pluginSectionViews.Values.Where(item => item.Section.PluginId == SmokeRemoteId).ToArray();
        SmokeAssert(views.Length == 1, $"The sidebar has {views.Length} sections for {SmokeRemoteId}, expected 1");
        var view = views[0];
        SmokeAssert(AutomationProperties.GetAutomationId(view.Root) == SmokeRemoteSectionId,
            $"Section automation id is {AutomationProperties.GetAutomationId(view.Root)}, expected {SmokeRemoteSectionId}");
        SmokeAssert(PluginSectionsHost.Children.Contains(view.Root), $"{SmokeRemoteSectionId} is not in the sidebar's plugin sections");
        var header = SmokeLogicalDescendants<TextBlock>(view.Root)
            .FirstOrDefault(text => AutomationProperties.GetAutomationId(text) == SmokeRemoteSectionId + ".Header");
        SmokeAssert(header?.Text == "Connections", $"Section header reads '{header?.Text}', expected 'Connections'");

        var sidebarWasVisible = workspace.Settings.SidebarVisible;
        workspace.Settings.SidebarVisible = true;
        ApplySidebarLayout();
        try
        {
            await SmokeRemoteWaitAsync(
                () => view.Body.Content is FrameworkElement body
                    && SmokeFindById<TextBlock>(body, "Remote.SidebarEmpty") is { Visibility: Visibility.Visible },
                () => $"{SmokeRemoteSectionId} to show Remote.SidebarEmpty, shows "
                    + (view.Body.Content is FrameworkElement shown
                        ? string.Join(" | ", SmokeLogicalDescendants<TextBlock>(shown)
                            .Where(text => text.Visibility == Visibility.Visible && !string.IsNullOrEmpty(text.Text))
                            .Select(text => text.Text)) is { Length: > 0 } texts ? texts : shown.GetType().FullName
                        : view.Body.Content?.GetType().FullName ?? "nothing"));
        }
        finally
        {
            workspace.Settings.SidebarVisible = sidebarWasVisible;
            ApplySidebarLayout();
        }
    }

    /// <summary>The first element under <paramref name="root"/> (or the root itself) with this automation id, searching
    /// the code-built tree first and then the realized visual tree.</summary>
    private static T? SmokeFindById<T>(DependencyObject root, string automationId) where T : DependencyObject =>
        (root is T self ? [self] : Enumerable.Empty<T>())
            .Concat(SmokeLogicalDescendants<T>(root))
            .Concat(SmokeDescendants<T>(root))
            .FirstOrDefault(item => AutomationProperties.GetAutomationId(item) == automationId);

    /// <summary>The content dialog open on <paramref name="root"/>, or null.</summary>
    private static ContentDialog? SmokeOpenDialog(XamlRoot root) =>
        VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Select(popup => popup.Child).OfType<ContentDialog>().FirstOrDefault();

    private static Task SmokeRemoteWaitAsync(Func<bool> condition, Func<string> what) =>
        SmokeWaitWithinAsync(() => Task.FromResult(condition()), SmokeRemoteWait, what);
}
#endif
