#if DEBUG
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Trust;

namespace SimpleFile.App;

// The bundled FTP/SFTP plugin (com.sumafile.remote) end to end in the real host, against the SFTP test server that
// scripts/smoke-winui-ui.ps1 starts. Excluded from release builds.
public sealed partial class MainWindow
{
    private const string SmokeRemoteId = "com.sumafile.remote";
    private const string SmokeRemoteManagerCommand = "plugin:" + SmokeRemoteId + ":ftp-sftp-manager";
    private const string SmokeRemoteSectionId = "PluginSection." + SmokeRemoteId + ".connections";
    private const string SmokeRemoteManagerTitle = "FTP/SFTP manager";
    private const string SmokeRemoteLegacyProfile = "Legacy SFTP";
    private const string SmokeRemoteProfile = "Smoke SFTP";
    private const string SmokeRemoteBandwidthKey = "plugin." + SmokeRemoteId + ".bandwidthMib";
    private const string SmokeRemoteCredentialPrefix = "SumaFile.Remote.";
    private const string SmokeRemoteCredentialFile = "remote-credential.txt";
    private const string SmokeRemoteBackendName = "sumafile-remote-backend";
    private const string SmokeRemoteBackendStopped = "Plugin backend stopped.";
    private const string SmokeRemoteUnicodeFolder = "café 資料";
    private const string SmokeRemoteLiteralFile = "literal [brackets] & spaces.txt";
    private static readonly TimeSpan SmokeRemoteWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SmokeRemoteTransferWait = TimeSpan.FromSeconds(30);

    /// <summary>The SFTP test server the smoke script started (its <c>--info</c> file).</summary>
    private sealed record SmokeRemoteServer(int Port, string Fingerprint, string User, string Password, string Root, string? LegacyProfileId);

    /// <summary>
    /// "with": the whole FTP/SFTP plugin chain against the SFTP test server, in the order a user meets it: the
    /// bundled plugin trusted through bundled.json, the profile imported from the smoke's 1.0.4 database in the
    /// Connections section, the manager from the command and from a sidebar row, a new profile with a saved password,
    /// host-key trust, browsing, upload, download, Cancel from the Transfers row, folder operations, disable during
    /// a transfer, profile delete and the settings page. Every wait is bounded and names what it waited for.
    /// "none": no remote UI anywhere.
    /// </summary>
    private async Task SmokeRemoteAsync(List<object> results, string mode)
    {
        if (mode == "none")
        {
            await SmokeRemoteNoneAsync(results);
            return;
        }

        if (mode != "with")
        {
            return;
        }

        var manager = _plugins ?? throw new InvalidOperationException("Plugin manager was not created");
        var workspace = _workspace ?? throw new InvalidOperationException("Workspace is not available");
        var fileOps = workspace.FileOps ?? throw new InvalidOperationException("File operations are not available");
        var transfers = _transfer ?? throw new InvalidOperationException("The transfer manager is not available");
        var server = SmokeRemoteServerInfo();
        var localRoot = Environment.GetEnvironmentVariable("SUMAFILE_SMOKE_REMOTE_LOCAL");
        SmokeAssert(!string.IsNullOrWhiteSpace(localRoot) && File.Exists(Path.Combine(localRoot, "upload.txt")),
            $"SUMAFILE_SMOKE_REMOTE_LOCAL does not hold upload.txt: '{localRoot}'");
        localRoot = Path.GetFullPath(localRoot!);

        // 1. Discovered from <exe dir>\plugins and Enabled through bundled.json; the approvals file holds nothing for it.
        var record = manager.Plugins.FirstOrDefault(item => item.Id == SmokeRemoteId);
        SmokeAssert(record is not null, $"Plugin {SmokeRemoteId} was not discovered");
        SmokeAssert(record!.State == PluginState.Enabled, $"Plugin {SmokeRemoteId} is {record.State}, expected Enabled: {record.Error}");
        var installRoot = PluginDiscovery.InstallRoot();
        var installFolder = Path.Combine(installRoot, SmokeRemoteId);
        SmokeAssert(SmokeSameFolder(record.Discovered.Folder, installFolder),
            $"Plugin {SmokeRemoteId} was discovered in {record.Discovered.Folder}, expected {installFolder}");
        var approvalsPath = Environment.GetEnvironmentVariable(PluginApprovalStore.PathVariable);
        SmokeAssert(!string.IsNullOrWhiteSpace(approvalsPath), $"{PluginApprovalStore.PathVariable} is not set");
        SmokeAssert(!File.Exists(approvalsPath) || !File.ReadAllText(approvalsPath!).Contains(SmokeRemoteId, StringComparison.Ordinal),
            $"The approvals file {approvalsPath} holds a record for {SmokeRemoteId}; it must be trusted through bundled.json alone");
        SmokeAssert(AppCommandCatalog.All.Any(command => command.Id == SmokeRemoteManagerCommand), $"The palette is missing {SmokeRemoteManagerCommand}");
        SmokeAssert(!AppCommandCatalog.All.Any(command => command.Id == "ftp-sftp-manager"), "The palette still has the bare ftp-sftp-manager command");

        // The sidebar stays shown for the whole scenario so the Connections rows are realized.
        var sidebarWasVisible = workspace.Settings.SidebarVisible;
        workspace.Settings.SidebarVisible = true;
        ApplySidebarLayout();
        try
        {
            // 2. One Connections section, listing exactly the profile imported from the smoke's 1.0.4 database.
            SmokeRemoteSectionHeader();
            await SmokeRemoteWaitRowsAsync(SmokeRemoteSidebarList, [SmokeRemoteLegacyProfile], "Remote.SidebarList");
            var profilesDb = Path.Combine(UiSmokeRoot()!, "plugins-data", SmokeRemoteId, "remote-profiles.db");
            SmokeAssert(File.Exists(profilesDb), $"{profilesDb} does not exist");

            // 3. The command opens one manager window; running it again keeps it. A sidebar row opens it on its profile.
            var (window, root) = await SmokeRemoteOpenManagerAsync(() => RunAppCommandAsync(SmokeRemoteManagerCommand), SmokeRemoteManagerCommand);
            var type = window.GetType();
            SmokeAssert(type.FullName == "SumaFile.Remote.Views.RemoteManagerWindow", $"The manager window is a {type.FullName}");
            var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(type.Assembly);
            SmokeAssert(context is not null && !ReferenceEquals(context, System.Runtime.Loader.AssemblyLoadContext.Default),
                $"RemoteManagerWindow was loaded into the {context?.Name ?? "unknown"} load context, not the plugin's");
            await SmokeRemoteWaitRowsAsync(() => SmokeFindById<ListView>(root, "Remote.ProfileList"), [SmokeRemoteLegacyProfile], "Remote.ProfileList");
            await RunAppCommandAsync(SmokeRemoteManagerCommand).WaitAsync(SmokeRemoteWait);
            await SmokeRemoteKeepsOneWindowAsync(window, $"Running {SmokeRemoteManagerCommand} again");

            await SmokeRemoteCloseManagerAsync(window);
            (window, root) = await SmokeRemoteOpenManagerAsync(
                () => SmokeActivateRowAsync(SmokeRemoteSidebarList() ?? throw new InvalidOperationException("Remote.SidebarList is missing"), SmokeRemoteLegacyProfile),
                "a click on the Legacy SFTP sidebar row");
            await SmokeRemoteWaitAsync(
                () => SmokeRow(SmokeFindById<ListView>(root, "Remote.ProfileList"), SmokeRemoteLegacyProfile) is { IsSelected: true },
                () => "the Legacy SFTP row selected in Remote.ProfileList after the sidebar click");
            await SmokeActivateRowAsync(SmokeRemoteSidebarList()!, SmokeRemoteLegacyProfile);
            await SmokeRemoteKeepsOneWindowAsync(window, "Activating the Legacy SFTP sidebar row again");

            var stats = await SmokeRemoteProfileFlowAsync(manager, fileOps, transfers, server, localRoot, installRoot, window, root);
            results.Add(new
            {
                scenario = "remote",
                mode,
                legacyProfile = SmokeRemoteLegacyProfile,
                upload = stats.Uploaded,
                download = stats.Downloaded,
                cancelledAtPercent = stats.CancelledAtPercent,
                disabledRow = stats.DisabledRow,
                stoppedBackendPid = stats.StoppedBackendPid,
                passed = true,
            });
        }
        finally
        {
            workspace.Settings.SidebarVisible = sidebarWasVisible;
            ApplySidebarLayout();
        }
    }

    private sealed record SmokeRemoteStats(string Uploaded, string Downloaded, double CancelledAtPercent, string DisabledRow, int StoppedBackendPid);

    /// <summary>
    /// Steps 4 to 12, which create a Credential Manager entry. Whatever happens, the credential cleanup runs: it
    /// deletes the target of the profile the smoke saved and fails, without deleting it, on any other new
    /// <c>SumaFile.Remote.*</c> target.
    /// </summary>
    private async Task<SmokeRemoteStats> SmokeRemoteProfileFlowAsync(
        PluginManager manager,
        FileOperationService fileOps,
        TransferManagerViewModel transfers,
        SmokeRemoteServer server,
        string localRoot,
        string installRoot,
        Window window,
        FrameworkElement root)
    {
        var baseline = SmokeRemoteCredentialTargets();
        string? smokeTarget = null;
        Exception? stepFailure = null;
        SmokeRemoteStats? stats = null;
        try
        {
            // 4. A new SFTP profile with its password saved in Credential Manager.
            var newProfile = await SmokeRemoteEnabledButtonAsync(root, "Remote.NewProfile");
            SmokeInvoke(newProfile);
            var dialog = await SmokeOpenDialogAsync(root.XamlRoot!, "New remote profile");
            SmokeAssert(Content?.XamlRoot is not { } mainRoot
                || !VisualTreeHelper.GetOpenPopupsForXamlRoot(mainRoot).Any(popup => ReferenceEquals(popup.Child, dialog)),
                "The profile dialog opened on the main window");
            SmokeRemoteControl<TextBox>(dialog, "RemoteProfile.Name").Text = SmokeRemoteProfile;
            var protocol = SmokeRemoteControl<ComboBox>(dialog, "RemoteProfile.Protocol");
            protocol.SelectedItem = protocol.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "sftp"));
            SmokeRemoteControl<TextBox>(dialog, "RemoteProfile.Host").Text = "127.0.0.1";
            SmokeRemoteControl<NumberBox>(dialog, "RemoteProfile.Port").Value = server.Port;
            SmokeRemoteControl<TextBox>(dialog, "RemoteProfile.User").Text = server.User;
            SmokeRemoteControl<PasswordBox>(dialog, "RemoteProfile.Secret").Password = server.Password;
            SmokeRemoteControl<CheckBox>(dialog, "RemoteProfile.SaveSecret").IsChecked = true;
            SmokeInvoke(SmokeDialogButton(dialog, "PrimaryButton"));
            await SmokeRemoteWaitAsync(() => SmokeOpenDialog(root.XamlRoot!) is null, () => "the profile dialog to close after Save");
            var profileList = () => SmokeFindById<ListView>(root, "Remote.ProfileList");
            await SmokeRemoteWaitRowsAsync(profileList, [SmokeRemoteLegacyProfile, SmokeRemoteProfile], "Remote.ProfileList after Save");
            await SmokeRemoteWaitRowsAsync(SmokeRemoteSidebarList, [SmokeRemoteLegacyProfile, SmokeRemoteProfile], "Remote.SidebarList after Save",
                TimeSpan.FromSeconds(5));
            var profileId = SmokeRow(profileList(), SmokeRemoteProfile)?.Tag as string;
            SmokeAssert(!string.IsNullOrWhiteSpace(profileId), "The Smoke SFTP row carries no profile id");
            smokeTarget = SmokeRemoteCredentialPrefix + profileId;
            // For scripts/smoke-winui-ui.ps1, which removes this target itself if this App dies before its cleanup.
            File.WriteAllText(Path.Combine(UiSmokeRoot()!, SmokeRemoteCredentialFile), smokeTarget);
            var gained = SmokeRemoteCredentialTargets().Except(baseline, StringComparer.OrdinalIgnoreCase).ToArray();
            SmokeAssert(gained.Length == 1 && string.Equals(gained[0], smokeTarget, StringComparison.OrdinalIgnoreCase),
                $"Saving {SmokeRemoteProfile} added the credentials [{string.Join(", ", gained)}], expected exactly {smokeTarget}");

            // 5. Connect: the host-key dialog shows the server's fingerprint; trusting it lists the server root.
            SmokeAssert(SmokeRow(profileList(), SmokeRemoteProfile) is { IsSelected: true }, "Smoke SFTP is not selected after Save");
            SmokeInvoke(await SmokeRemoteEnabledButtonAsync(root, "Remote.Connect"));
            var hostKey = await SmokeOpenDialogAsync(root.XamlRoot!, "Trust this server?");
            var fingerprint = SmokeRemoteControl<TextBlock>(hostKey, "RemoteHostKey.Fingerprint").Text;
            SmokeAssert(fingerprint == server.Fingerprint, $"The host-key dialog shows '{fingerprint}', expected '{server.Fingerprint}'");
            SmokeInvoke(SmokeDialogButton(hostKey, "PrimaryButton"));
            var remoteList = () => SmokeFindById<ListView>(root, "Remote.RemoteList");
            string[] rootNames = [SmokeRemoteUnicodeFolder, SmokeRemoteLiteralFile, "big.bin"];
            await SmokeRemoteWaitRowsAsync(remoteList, rootNames, "Remote.RemoteList after Connect", SmokeRemoteTransferWait);
            var remotePath = SmokeRemoteControl<TextBox>(root, "Remote.RemotePath");
            SmokeAssert(remotePath.Text == "/", $"Remote.RemotePath reads '{remotePath.Text}' after Connect, expected '/'");
            await SmokeActivateRowAsync(remoteList()!, SmokeRemoteUnicodeFolder);
            await SmokeRemoteWaitAsync(() => remotePath.Text == "/" + SmokeRemoteUnicodeFolder,
                () => $"Remote.RemotePath to read '/{SmokeRemoteUnicodeFolder}', reads '{remotePath.Text}'");
            await SmokeRemoteWaitRowsAsync(remoteList, ["hello.txt"], $"Remote.RemoteList in /{SmokeRemoteUnicodeFolder}");
            var remoteUp = SmokeLogicalDescendants<Button>(root)
                .Single(button => AutomationProperties.GetName(button) == "Go to parent remote folder");
            await SmokeRemoteWaitAsync(() => remoteUp.IsEnabled, () => "'Go to parent remote folder' to be enabled");
            SmokeInvoke(remoteUp);
            await SmokeRemoteWaitAsync(() => remotePath.Text == "/", () => $"Remote.RemotePath to read '/' again, reads '{remotePath.Text}'");
            await SmokeRemoteWaitRowsAsync(remoteList, rootNames, "Remote.RemoteList back at /");

            // 6. Upload upload.txt from the typed local folder.
            var localList = () => SmokeFindById<ListView>(root, "Remote.LocalList");
            var localPath = SmokeRemoteControl<TextBox>(root, "Remote.LocalPath");
            localPath.Text = localRoot;
            SmokeInvoke(SmokeRemoteControl<Button>(root, "Remote.LocalGo"));
            await SmokeRemoteWaitRowsAsync(localList, ["upload.txt", "download"], "Remote.LocalList in remote-local");
            await SmokeSelectRowAsync(localList, "upload.txt");
            var uploadTitle = "Uploading 1 item to 127.0.0.1:/";
            var upload = await SmokeRemoteTransferAsync(transfers, root, "Remote.Upload", uploadTitle);
            await SmokeAwaitRowAsync(upload, SmokeRemoteTransferWait, uploadTitle);
            SmokeAssert(upload.Status == TransferOperationStatus.Completed, $"{uploadTitle} ended {upload.Status}: {upload.ErrorMessage}");
            SmokeAssert(upload.IsExternal && !upload.CanChangeOptions, $"{uploadTitle} offers per-task Options");
            var uploaded = Path.Combine(server.Root, "upload.txt");
            SmokeAssert(File.Exists(uploaded) && File.ReadAllText(uploaded) == "upload", $"{uploaded} does not read 'upload'");

            // 7. Download the punctuated file into remote-local.
            await SmokeSelectRowAsync(remoteList, SmokeRemoteLiteralFile);
            var downloadTitle = "Downloading 1 item from 127.0.0.1";
            var download = await SmokeRemoteTransferAsync(transfers, root, "Remote.Download", downloadTitle);
            await SmokeAwaitRowAsync(download, SmokeRemoteTransferWait, downloadTitle);
            SmokeAssert(download.Status == TransferOperationStatus.Completed, $"{downloadTitle} ended {download.Status}: {download.ErrorMessage}");
            var downloaded = Path.Combine(localRoot, SmokeRemoteLiteralFile);
            SmokeAssert(File.Exists(downloaded) && File.ReadAllText(downloaded) == "literal", $"{downloaded} does not read 'literal'");

            // 8. Cancel a throttled download over an existing file from its Transfers row.
            var downloadFolder = Path.Combine(localRoot, "download");
            var existing = Path.Combine(downloadFolder, "big.bin");
            var existingBytes = File.ReadAllBytes(existing);
            double cancelledAtPercent;
            await fileOps.SetSettingAsync(SmokeRemoteBandwidthKey, "1").WaitAsync(SmokeRemoteWait);
            try
            {
                await SmokeActivateRowAsync(localList()!, "download");
                await SmokeRemoteWaitAsync(() => SmokeSameFolder(localPath.Text, downloadFolder),
                    () => $"Remote.LocalPath to read {downloadFolder}, reads '{localPath.Text}'");
                var conflict = SmokeRemoteControl<ComboBox>(root, "Remote.Conflict");
                conflict.SelectedItem = conflict.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "replace"));
                await SmokeSelectRowAsync(remoteList, "big.bin");
                var cancelRow = await SmokeRemoteTransferAsync(transfers, root, "Remote.Download", downloadTitle);
                SmokeAssert(cancelRow.CanCancel && !cancelRow.CanChangeOptions,
                    $"The running download row offers Cancel {cancelRow.CanCancel}, Options {cancelRow.CanChangeOptions}");
                await SmokeWaitWithinAsync(
                    () => Task.FromResult(cancelRow.IsTerminal || cancelRow.ProgressPercent > 0),
                    SmokeRemoteTransferWait,
                    () => $"the big.bin download to report progress: {cancelRow.Status} at {cancelRow.ProgressPercent}%");
                SmokeAssert(!cancelRow.IsTerminal, $"The big.bin download ended {cancelRow.Status} before Cancel: {cancelRow.ErrorMessage}");
                SmokeAssert(cancelRow.CanCancel && !cancelRow.CanChangeOptions,
                    $"The running download row offers Cancel {cancelRow.CanCancel}, Options {cancelRow.CanChangeOptions}");
                cancelledAtPercent = cancelRow.ProgressPercent;
                transfers.Cancel(cancelRow);
                SmokeAssert(cancelRow.Status == TransferOperationStatus.Cancelling, $"The row is {cancelRow.Status} right after Cancel, expected Cancelling");
                await SmokeAwaitRowAsync(cancelRow, SmokeRemoteTransferWait, "cancel the big.bin download");
                SmokeAssert(cancelRow.Status == TransferOperationStatus.Cancelled, $"The cancelled download ended {cancelRow.Status}: {cancelRow.ErrorMessage}");
                SmokeAssert(File.ReadAllBytes(existing).AsSpan().SequenceEqual(existingBytes), $"Cancel changed the existing {existing}");
                var partials = Directory.EnumerateFileSystemEntries(downloadFolder)
                    .Where(path => Path.GetFileName(path).Contains(".sumafile-partial", StringComparison.Ordinal)).ToArray();
                SmokeAssert(partials.Length == 0, "Cancel left partial files: " + string.Join(", ", partials));
            }
            finally
            {
                await fileOps.SetSettingAsync(SmokeRemoteBandwidthKey, "0").WaitAsync(SmokeRemoteWait);
            }

            // 9. Create, rename and delete a remote folder through the name prompt and the confirmation.
            SmokeInvoke(await SmokeRemoteEnabledButtonAsync(root, "Remote.NewFolder"));
            await SmokeRemoteAnswerNamePromptAsync(root, "New remote folder", "smoke-folder");
            await SmokeRemoteWaitAsync(() => SmokeRow(remoteList(), "smoke-folder") is not null, () => "the smoke-folder row");
            SmokeAssert(Directory.Exists(Path.Combine(server.Root, "smoke-folder")), "The server has no smoke-folder");
            await SmokeSelectRowAsync(remoteList, "smoke-folder");
            SmokeInvoke(await SmokeRemoteEnabledButtonAsync(root, "Remote.Rename"));
            const string renamed = "renamed folder [ok]";
            await SmokeRemoteAnswerNamePromptAsync(root, "Rename remote item", renamed);
            await SmokeRemoteWaitAsync(() => SmokeRow(remoteList(), renamed) is not null && SmokeRow(remoteList(), "smoke-folder") is null,
                () => $"the '{renamed}' row in place of smoke-folder");
            SmokeAssert(Directory.Exists(Path.Combine(server.Root, renamed)), $"The server has no '{renamed}' folder");
            await SmokeSelectRowAsync(remoteList, renamed);
            SmokeInvoke(await SmokeRemoteEnabledButtonAsync(root, "Remote.DeleteEntries"));
            var confirmDelete = await SmokeOpenDialogAsync(root.XamlRoot!, "Delete remote item");
            SmokeInvoke(SmokeDialogButton(confirmDelete, "PrimaryButton"));
            await SmokeRemoteWaitAsync(() => SmokeRow(remoteList(), renamed) is null && !Directory.Exists(Path.Combine(server.Root, renamed)),
                () => $"'{renamed}' to leave the list and the server");

            // 10. Disable the plugin while a throttled download runs.
            var (disabledRow, stoppedBackend) = await SmokeRemoteDisableDuringTransferAsync(manager, fileOps, transfers, installRoot, window, root, downloadTitle);

            // 11. Delete the smoke profile from a fresh manager window: its saved password goes with it.
            (window, root) = await SmokeRemoteOpenManagerAsync(() => RunAppCommandAsync(SmokeRemoteManagerCommand), "the command after re-enable");
            profileList = () => SmokeFindById<ListView>(root, "Remote.ProfileList");
            await SmokeRemoteWaitRowsAsync(profileList, [SmokeRemoteLegacyProfile, SmokeRemoteProfile], "Remote.ProfileList after re-enable");
            await SmokeSelectRowAsync(profileList, SmokeRemoteProfile);
            SmokeInvoke(await SmokeRemoteEnabledButtonAsync(root, "Remote.DeleteProfile"));
            var confirmProfile = await SmokeOpenDialogAsync(root.XamlRoot!, "Delete remote profile");
            SmokeInvoke(SmokeDialogButton(confirmProfile, "PrimaryButton"));
            await SmokeRemoteWaitRowsAsync(profileList, [SmokeRemoteLegacyProfile], "Remote.ProfileList after the delete");
            await SmokeRemoteWaitRowsAsync(SmokeRemoteSidebarList, [SmokeRemoteLegacyProfile], "Remote.SidebarList after the delete");
            var current = SmokeRemoteCredentialTargets();
            var left = current.Except(baseline, StringComparer.OrdinalIgnoreCase).ToArray();
            SmokeAssert(left.Length == 0, $"Deleting {SmokeRemoteProfile} left the credentials [{string.Join(", ", left)}]");
            // And it deleted only its own: every target that was there before the smoke still is.
            var vanished = baseline.Where(target => !current.Contains(target, StringComparer.OrdinalIgnoreCase)).ToArray();
            SmokeAssert(baseline.All(target => current.Contains(target, StringComparer.OrdinalIgnoreCase)),
                $"Deleting {SmokeRemoteProfile} also removed the credentials [{string.Join(", ", vanished)}]");
            await SmokeRemoteCloseManagerAsync(window);

            // 12. Settings > Plugins: the declarative transfer settings and the custom page, whose button opens the manager.
            await SmokeRemoteSettingsPageAsync();

            stats = new SmokeRemoteStats(upload.Status.ToString(), download.Status.ToString(), cancelledAtPercent, disabledRow, stoppedBackend);
        }
        catch (Exception exception)
        {
            stepFailure = exception;
        }

        // The credential cleanup, whether or not a step failed. A cleanup failure is kept beside a step failure.
        string[] stray = [];
        Exception? cleanupFailure = null;
        try
        {
            if (smokeTarget is null)
            {
                var id = SmokeRow(PluginWindowHostFor(SmokeRemoteId)?.OpenWindows.FirstOrDefault()?.Content is FrameworkElement open
                        ? SmokeFindById<ListView>(open, "Remote.ProfileList") : null, SmokeRemoteProfile)?.Tag as string
                    ?? SmokeRow(SmokeRemoteSidebarList(), SmokeRemoteProfile)?.Tag as string;
                smokeTarget = string.IsNullOrWhiteSpace(id) ? null : SmokeRemoteCredentialPrefix + id;
            }

            if (smokeTarget is not null)
            {
                SmokeDeleteRemoteCredential(smokeTarget);
            }

            stray = [.. SmokeRemoteCredentialTargets()
                .Except(baseline, StringComparer.OrdinalIgnoreCase)
                .Where(target => !string.Equals(target, smokeTarget, StringComparison.OrdinalIgnoreCase))];
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        if (stray.Length > 0)
        {
            var message = "The remote smoke left Credential Manager targets it cannot attribute to its own profile (not deleted): "
                + string.Join(", ", stray);
            Exception[] inner = [.. new[] { stepFailure, cleanupFailure }.OfType<Exception>()];
            throw inner.Length == 0 ? new InvalidOperationException(message) : new AggregateException(message, inner);
        }

        if (stepFailure is not null && cleanupFailure is not null)
        {
            throw new AggregateException(stepFailure, cleanupFailure);
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Throw(cleanupFailure);
        }

        if (stepFailure is not null)
        {
            ExceptionDispatchInfo.Throw(stepFailure);
        }

        return stats!;
    }

    /// <summary>
    /// Step 10: a throttled download runs when the plugin is disabled. The manager closes, the row fails with the
    /// host's stopped text, the backend process is gone, and the section and command leave; enabling brings the
    /// section back.
    /// </summary>
    private async Task<(string Row, int StoppedBackendPid)> SmokeRemoteDisableDuringTransferAsync(
        PluginManager manager,
        FileOperationService fileOps,
        TransferManagerViewModel transfers,
        string installRoot,
        Window window,
        FrameworkElement root,
        string downloadTitle)
    {
        var remoteList = () => SmokeFindById<ListView>(root, "Remote.RemoteList");
        var backends = SmokeProcessIdsUnder(SmokeRemoteBackendName, installRoot);
        SmokeAssert(backends.Length == 1, $"Expected one {SmokeRemoteBackendName} under {installRoot} before the disable, found {backends.Length}");
        await fileOps.SetSettingAsync(SmokeRemoteBandwidthKey, "1").WaitAsync(SmokeRemoteWait);
        try
        {
            await SmokeSelectRowAsync(remoteList, "big.bin");
            var row = await SmokeRemoteTransferAsync(transfers, root, "Remote.Download", downloadTitle);
            await SmokeWaitWithinAsync(
                () => Task.FromResult(row.IsTerminal || row.ProgressPercent > 0),
                SmokeRemoteTransferWait,
                () => $"the second big.bin download to report progress: {row.Status} at {row.ProgressPercent}%");
            SmokeAssert(!row.IsTerminal, $"The second big.bin download ended {row.Status} before the disable: {row.ErrorMessage}");
            var closed = false;
            window.Closed += (_, _) => closed = true;

            await manager.DisableAsync(SmokeRemoteId, CancellationToken.None).WaitAsync(SmokeRemoteWait);
            await SmokeRemoteWaitAsync(
                () => closed
                    && PluginWindowHostFor(SmokeRemoteId)?.OpenWindows is not { Count: > 0 }
                    && row.Status == TransferOperationStatus.Failed
                    && row.ErrorMessage == SmokeRemoteBackendStopped
                    && SmokeProcessIdsUnder(SmokeRemoteBackendName, installRoot).Length == 0
                    && !SmokeRemoteHasSection()
                    && !AppCommandCatalog.All.Any(command => command.Id == SmokeRemoteManagerCommand),
                () => $"10 s after the disable: window closed {closed}, row {row.Status} '{row.ErrorMessage}', "
                    + $"backends {SmokeProcessIdsUnder(SmokeRemoteBackendName, installRoot).Length}, section {SmokeRemoteHasSection()}, "
                    + $"command {AppCommandCatalog.All.Any(command => command.Id == SmokeRemoteManagerCommand)}");
            SmokeAssert(Content?.XamlRoot is not { } mainRoot || SmokeOpenDialog(mainRoot) is null,
                $"A dialog opened on the main window during the disable: {(Content?.XamlRoot is { } shown ? SmokeOpenDialog(shown)?.Title : null)}");

            await manager.EnableAsync(SmokeRemoteId, CancellationToken.None).WaitAsync(SmokeRemoteWait);
            await SmokeRemoteWaitAsync(SmokeRemoteHasSection, () => $"{SmokeRemoteSectionId} to return after the re-enable");
            SmokeAssert(Content?.XamlRoot is not { } enabledRoot || SmokeOpenDialog(enabledRoot) is null,
                "A consent dialog opened on the main window after the re-enable");
            var record = manager.Plugins.FirstOrDefault(item => item.Id == SmokeRemoteId);
            SmokeAssert(record?.State == PluginState.Enabled, $"{SmokeRemoteId} is {record?.State} after the re-enable: {record?.Error}");
            SmokeAssert(AppCommandCatalog.All.Any(command => command.Id == SmokeRemoteManagerCommand), $"{SmokeRemoteManagerCommand} is missing after the re-enable");
            return ($"{row.Status}: {row.ErrorMessage}", backends[0]);
        }
        finally
        {
            await fileOps.SetSettingAsync(SmokeRemoteBandwidthKey, "0").WaitAsync(SmokeRemoteWait);
        }
    }

    /// <summary>
    /// Step 12: the remote card in Settings &gt; Plugins shows the declarative bandwidth control and the plugin's own
    /// page, whose button opens exactly one manager window. Closes the Settings window and the manager.
    /// </summary>
    private async Task SmokeRemoteSettingsPageAsync()
    {
        const string bandwidthLabel = "Bandwidth limit (MB/s, 0 = unlimited)";
        var slotId = PluginSettingsPanelBuilder.SettingsPageAutomationId(SmokeRemoteId);
        var showing = _fileOperationDialogs.ShowSettingsAsync("Plugins");
        try
        {
            SettingsWindow? settings = null;
            await SmokeRemoteWaitAsync(() => (settings = _fileOperationDialogs.OpenSettingsWindow) is { IsShown: true }
                && settings.Content is FrameworkElement { IsLoaded: true }
                && settings.SmokePluginsList.Children.Count > 0, () => "the Settings window with plugin cards");
            // The remote plugin's own page, under Plugins: its custom settings page is created when it is shown.
            settings!.SmokeSelectPlugin(SmokeRemoteId);
            await SmokeRemoteWaitAsync(() => settings.SmokeSelectedPlugin == SmokeRemoteId, () => "the remote plugin's Settings page");
            Button? open = null;
            var hasBandwidth = false;
            await SmokeRemoteWaitAsync(
                () =>
                {
                    // The panel re-renders on plugin changes, so look the card up again each time.
                    var card = settings!.SmokePluginsList.Children.OfType<FrameworkElement>().FirstOrDefault(item =>
                        SmokeLogicalDescendants<TextBlock>(item).Any(text => text.Text == "FTP/SFTP connections"));
                    hasBandwidth = card is not null
                        && SmokeLogicalDescendants<NumberBox>(card).Any(box => Equals(box.Header, bandwidthLabel));
                    var slot = card is null ? null : SmokeLogicalDescendants<ContentPresenter>(card)
                        .FirstOrDefault(item => AutomationProperties.GetAutomationId(item) == slotId);
                    open = slot is { Content: FrameworkElement page } ? SmokeFindById<Button>(page, "Remote.SettingsOpenManager") : null;
                    return hasBandwidth && open is { IsLoaded: true };
                },
                () => $"the remote card with '{bandwidthLabel}' ({hasBandwidth}) and {slotId} holding Remote.SettingsOpenManager ({open is not null})");
            SmokeInvoke(open!);
            Window? window = null;
            await SmokeRemoteWaitAsync(
                () => PluginWindowHostFor(SmokeRemoteId)?.OpenWindows is { Count: 1 } windows && (window = windows[0]) is not null,
                () => $"one manager window after Remote.SettingsOpenManager, found {PluginWindowHostFor(SmokeRemoteId)?.OpenWindows.Count ?? -1}");
            await SmokeRemoteKeepsOneWindowAsync(window!, "Remote.SettingsOpenManager");
            await SmokeRemoteCloseManagerAsync(window!);
        }
        finally
        {
            _fileOperationDialogs.CloseSettingsWindow();
            await showing.WaitAsync(SmokeRemoteWait);
        }
    }

    /// <summary>
    /// The remote backend is killed: the plugin faults and its section leaves; a restart brings both back.
    /// </summary>
    private async Task SmokeRemoteBackendLossAsync(List<object> results)
    {
        var manager = _plugins ?? throw new InvalidOperationException("Plugin manager was not created");
        var installRoot = PluginDiscovery.InstallRoot();
        if (SmokeProcessIdsUnder(SmokeRemoteBackendName, installRoot).Length == 0)
        {
            // first-call activation: any backend call starts it.
            await RunAppCommandAsync(SmokeRemoteManagerCommand).WaitAsync(SmokeRemoteWait);
            await SmokeWaitWithinAsync(
                () => Task.FromResult(SmokeProcessIdsUnder(SmokeRemoteBackendName, installRoot).Length == 1),
                TimeSpan.FromSeconds(15),
                () => $"{SmokeRemoteBackendName} to start under {installRoot}");
        }

        var killed = new List<int>();
        foreach (var pid in SmokeProcessIdsUnder(SmokeRemoteBackendName, installRoot))
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
            process.WaitForExit(5000);
            killed.Add(pid);
        }

        SmokeAssert(killed.Count == 1, $"Expected one {SmokeRemoteBackendName} under {installRoot}, killed {killed.Count}");
        PluginRecord? record = null;
        await SmokeWaitWithinAsync(
            () => Task.FromResult((record = manager.Plugins.FirstOrDefault(item => item.Id == SmokeRemoteId)) is { State: PluginState.Faulted }
                && !SmokeRemoteHasSection()),
            TimeSpan.FromSeconds(15),
            () => $"After the kill: {SmokeRemoteId} is {record?.State}, section shown {SmokeRemoteHasSection()}");
        var faulted = record!.State.ToString();

        await manager.RestartBackendAsync(SmokeRemoteId, _pluginCts?.Token ?? CancellationToken.None).WaitAsync(SmokeRemoteWait);
        await SmokeWaitWithinAsync(
            () => Task.FromResult(manager.Plugins.FirstOrDefault(item => item.Id == SmokeRemoteId) is { State: PluginState.Enabled }
                && SmokeRemoteHasSection()),
            TimeSpan.FromSeconds(15),
            () => $"After the restart: {SmokeRemoteId} is {manager.Plugins.FirstOrDefault(item => item.Id == SmokeRemoteId)?.State}, "
                + $"section shown {SmokeRemoteHasSection()}");
        foreach (var open in PluginWindowHostFor(SmokeRemoteId)?.OpenWindows.ToArray() ?? [])
        {
            await SmokeRemoteCloseManagerAsync(open);
        }

        results.Add(new { scenario = "remote-backend-loss", mode = "with", killedPid = killed[0], faulted, passed = true });
    }

    /// <summary>"none": no section, command, toolbar action, Tools menu item or Settings element for FTP/SFTP.</summary>
    private async Task SmokeRemoteNoneAsync(List<object> results)
    {
        SmokeAssert(PluginSectionsHost.Children.Count == 0, $"Unexpected plugin sidebar sections: {PluginSectionsHost.Children.Count}");
        var commands = AppCommandCatalog.All.Where(command => command.Id.Contains("ftp-sftp-manager", StringComparison.Ordinal))
            .Select(command => command.Id).ToArray();
        SmokeAssert(commands.Length == 0, "Commands still name ftp-sftp-manager: " + string.Join(", ", commands));
        var actions = ToolbarActionCatalog.All.Where(action => action.Id.Contains("ftp-sftp-manager", StringComparison.Ordinal)
                || (action.CommandId ?? "").Contains("ftp-sftp-manager", StringComparison.Ordinal))
            .Select(action => action.Id).ToArray();
        SmokeAssert(actions.Length == 0, "Toolbar actions still name ftp-sftp-manager: " + string.Join(", ", actions));
        var more = ContextMenuBuilder.BuildPaneMoreMenu(new ContextMenuRequest());
        var tools = more.SingleOrDefault(entry => entry.Id == "overflow-tools-menu");
        SmokeAssert(tools is not null, "The Pane More menu has no Tools menu");
        var flat = SmokeFlatten(more).Select(entry => entry.Id).ToArray();
        SmokeAssert(!flat.Contains("overflow-ftp-sftp-manager"), "The Pane More menu still has overflow-ftp-sftp-manager");

        var showing = _fileOperationDialogs.ShowSettingsAsync();
        try
        {
            SettingsWindow? settings = null;
            await SmokeRemoteWaitAsync(() => (settings = _fileOperationDialogs.OpenSettingsWindow) is { IsShown: true }
                && settings.Content is FrameworkElement { IsLoaded: true }, () => "the Settings window");
            var content = (FrameworkElement)settings!.Content;
            var named = content.FindName("SettingsRemoteManagerButton") is not null
                || SmokeLogicalDescendants<FrameworkElement>(content).Concat(SmokeDescendants<FrameworkElement>(content))
                    .Any(element => element.Name == "SettingsRemoteManagerButton");
            SmokeAssert(!named, "The Settings window has an element named SettingsRemoteManagerButton");
            var allTexts = SmokeLogicalDescendants<TextBlock>(content).Concat(SmokeDescendants<TextBlock>(content)).ToArray();
            // A positive control: the scan reaches the collapsed category panels, so the absence below means something.
            SmokeAssert(content.FindName("PluginsPanel") is Panel plugins && allTexts.Any(text => text.Text == "Plugins" && SmokeIsInside(text, plugins)),
                "The Settings scan did not reach the Plugins panel's heading");
            var texts = allTexts.Where(text => (text.Text ?? "").Contains("FTP/SFTP connections", StringComparison.Ordinal)).ToArray();
            SmokeAssert(texts.Length == 0, "The Settings window shows 'FTP/SFTP connections'");
        }
        finally
        {
            _fileOperationDialogs.CloseSettingsWindow();
            await showing.WaitAsync(SmokeRemoteWait);
        }

        results.Add(new { scenario = "remote", mode = "none", passed = true });
    }

    private static bool SmokeIsInside(DependencyObject element, DependencyObject ancestor) =>
        ReferenceEquals(element, ancestor) || SmokeLogicalDescendants<DependencyObject>(ancestor).Any(item => ReferenceEquals(item, element));

    private static IEnumerable<ContextMenuEntry> SmokeFlatten(IEnumerable<ContextMenuEntry> entries) =>
        entries.SelectMany(entry => new[] { entry }.Concat(SmokeFlatten(entry.Children)));

    private static SmokeRemoteServer SmokeRemoteServerInfo()
    {
        var path = Environment.GetEnvironmentVariable("SUMAFILE_SMOKE_REMOTE");
        SmokeAssert(!string.IsNullOrWhiteSpace(path) && File.Exists(path), $"SUMAFILE_SMOKE_REMOTE does not name the test server's info file: '{path}'");
        var info = JsonSerializer.Deserialize<SmokeRemoteServer>(File.ReadAllText(path!), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        SmokeAssert(info is { Port: > 0 } && !string.IsNullOrEmpty(info.Fingerprint) && Directory.Exists(info.Root),
            $"The test server info in {path} is incomplete");
        return info! with { Root = Path.GetFullPath(info!.Root) };
    }

    /// <summary>The one remote section's header reads Connections.</summary>
    private void SmokeRemoteSectionHeader()
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
    }

    private bool SmokeRemoteHasSection() => PluginSectionsHost.Children.OfType<FrameworkElement>()
        .Any(item => AutomationProperties.GetAutomationId(item) == SmokeRemoteSectionId);

    /// <summary>The Connections section's profile list, once its body has loaded; brought into view so rows realize.</summary>
    private ListView? SmokeRemoteSidebarList()
    {
        var view = _pluginSectionViews.Values.FirstOrDefault(item => item.Section.PluginId == SmokeRemoteId && !item.Removed);
        if (view?.Body.Content is not FrameworkElement body)
        {
            return null;
        }

        var list = SmokeFindById<ListView>(body, "Remote.SidebarList");
        if (list is { IsLoaded: true })
        {
            list.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }

        return list;
    }

    /// <summary>Runs <paramref name="open"/> and waits for exactly one loaded manager window.</summary>
    private async Task<(Window Window, FrameworkElement Root)> SmokeRemoteOpenManagerAsync(Func<Task> open, string how)
    {
        // Bounds every way the manager is opened: the command (steps 3 and 11) and a sidebar row.
        await open().WaitAsync(SmokeRemoteWait);
        Window? window = null;
        await SmokeRemoteWaitAsync(
            () => PluginWindowHostFor(SmokeRemoteId)?.OpenWindows is { Count: 1 } windows && (window = windows[0]) is not null,
            () => $"one window in {SmokeRemoteId}'s window host after {how}, found {PluginWindowHostFor(SmokeRemoteId)?.OpenWindows.Count ?? -1}");
        SmokeAssert(window!.Title == SmokeRemoteManagerTitle, $"The manager window is titled '{window.Title}', expected '{SmokeRemoteManagerTitle}'");
        FrameworkElement? root = null;
        await SmokeRemoteWaitAsync(
            () => (root = window.Content as FrameworkElement) is { IsLoaded: true, XamlRoot: not null }
                && SmokeFindById<Button>(root, "Remote.NewProfile") is { IsLoaded: true },
            () => "the manager window's content to load");
        return (window, root!);
    }

    /// <summary>For 750 ms the plugin keeps exactly <paramref name="window"/> open.</summary>
    private async Task SmokeRemoteKeepsOneWindowAsync(Window window, string what)
    {
        var settle = Stopwatch.StartNew();
        while (settle.Elapsed < TimeSpan.FromMilliseconds(750))
        {
            var open = PluginWindowHostFor(SmokeRemoteId)?.OpenWindows;
            SmokeAssert(open is { Count: 1 } && ReferenceEquals(open[0], window),
                $"{what} left {open?.Count ?? -1} manager windows, expected the same one");
            await Task.Delay(50);
        }
    }

    private async Task SmokeRemoteCloseManagerAsync(Window window)
    {
        window.Close();
        await SmokeRemoteWaitAsync(
            () => PluginWindowHostFor(SmokeRemoteId)?.OpenWindows is not { Count: > 0 },
            () => $"{SmokeRemoteId}'s window host to empty after the close, holds {PluginWindowHostFor(SmokeRemoteId)?.OpenWindows.Count ?? -1}");
    }

    /// <summary>Waits until <paramref name="list"/> shows exactly <paramref name="names"/> (in any order).</summary>
    private static Task SmokeRemoteWaitRowsAsync(Func<ListViewBase?> list, IReadOnlyCollection<string> names, string what, TimeSpan? timeout = null)
    {
        string[] shown = [];
        return SmokeWaitWithinAsync(
            () =>
            {
                shown = SmokeRowNames(list());
                return Task.FromResult(shown.Length == names.Count && names.All(shown.Contains));
            },
            timeout ?? SmokeRemoteWait,
            () => $"{what} to show exactly [{string.Join(" | ", names)}], shows [{string.Join(" | ", shown)}]");
    }

    /// <summary>The automation names of a list's rows; a row that is not realized reads "?".</summary>
    private static string[] SmokeRowNames(ListViewBase? list)
    {
        if (list is null)
        {
            return [];
        }

        var names = new string[list.Items.Count];
        for (var index = 0; index < names.Length; index++)
        {
            names[index] = list.ContainerFromIndex(index) is SelectorItem container ? AutomationProperties.GetName(container) : "?";
        }

        return names;
    }

    /// <summary>
    /// The realized container whose automation name is <paramref name="name"/>. The remote lists hold the plugin's model
    /// objects, so rows are read from their containers, never from <c>Items</c>.
    /// </summary>
    private static SelectorItem? SmokeRow(ListViewBase? list, string name)
    {
        if (list is null)
        {
            return null;
        }

        for (var index = 0; index < list.Items.Count; index++)
        {
            if (list.ContainerFromIndex(index) is SelectorItem container && AutomationProperties.GetName(container) == name)
            {
                return container;
            }
        }

        return null;
    }

    /// <summary>Waits for the row named <paramref name="name"/> to be realized, then makes it the list's only selection.</summary>
    private static async Task SmokeSelectRowAsync(Func<ListViewBase?> list, string name)
    {
        SelectorItem? row = null;
        await SmokeRemoteWaitAsync(() => (row = SmokeRow(list(), name)) is not null,
            () => $"a realized '{name}' row, rows [{string.Join(" | ", SmokeRowNames(list()))}]");
        var owner = list()!;
        owner.SelectedIndex = owner.IndexFromContainer(row);
        SmokeAssert(owner.SelectedItems.Count == 1 && row!.IsSelected,
            $"Selecting '{name}' in {AutomationProperties.GetAutomationId(owner)} selected {owner.SelectedItems.Count} rows");
    }

    /// <summary>
    /// Raises the list's real <c>ItemClick</c> for the row named <paramref name="name"/>. List item automation peers
    /// expose no Invoke pattern, so this focuses the row with the keyboard and posts Enter to its window's focused
    /// input HWND, which <c>ListViewBase</c> turns into <c>ItemClick</c>.
    /// </summary>
    private async Task SmokeActivateRowAsync(ListViewBase list, string name)
    {
        SelectorItem? row = null;
        await SmokeRemoteWaitAsync(() => (row = SmokeRow(list, name)) is { IsLoaded: true },
            () => $"a realized '{name}' row in {AutomationProperties.GetAutomationId(list)}, rows [{string.Join(" | ", SmokeRowNames(list))}]");
        var owner = SmokeOwnerWindow(list) ?? throw new InvalidOperationException($"No window hosts {AutomationProperties.GetAutomationId(list)}");
        var top = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        if (GetActiveWindow() != top)
        {
            SetActiveWindow(top);
        }

        row!.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        await SmokeRemoteWaitAsync(
            () =>
            {
                if (!ReferenceEquals(FocusManager.GetFocusedElement(list.XamlRoot), row))
                {
                    row.Focus(FocusState.Keyboard);
                }

                return ReferenceEquals(FocusManager.GetFocusedElement(list.XamlRoot), row);
            },
            () => $"keyboard focus on the '{name}' row, focus is on {FocusManager.GetFocusedElement(list.XamlRoot)?.GetType().Name ?? "nothing"}");
        var target = SmokeFocusedInputWindow(top);
        const uint WM_KEYDOWN = 0x0100;
        const uint WM_KEYUP = 0x0101;
        const int VK_RETURN = 0x0D;
        var scan = (long)MapVirtualKeyW(VK_RETURN, 0) << 16;
        SmokeAssert(PostMessageW(target, WM_KEYDOWN, VK_RETURN, (nint)(1 | scan)), $"PostMessage(WM_KEYDOWN) failed: {Marshal.GetLastWin32Error()}");
        SmokeAssert(PostMessageW(target, WM_KEYUP, VK_RETURN, (nint)(1 | scan | (1L << 30) | (1L << 31))),
            $"PostMessage(WM_KEYUP) failed: {Marshal.GetLastWin32Error()}");
    }

    /// <summary>The main window or the remote plugin window whose content shares the list's XamlRoot.</summary>
    private Window? SmokeOwnerWindow(UIElement element) =>
        new Window[] { this }.Concat(PluginWindowHostFor(SmokeRemoteId)?.OpenWindows ?? [])
            .FirstOrDefault(window => window.Content is UIElement content && ReferenceEquals(content.XamlRoot, element.XamlRoot));

    /// <summary>
    /// The HWND that has keyboard focus inside <paramref name="top"/>: the thread's focus window when it lies in that
    /// window, otherwise the window's XAML input site, which is then given the focus.
    /// </summary>
    private static nint SmokeFocusedInputWindow(nint top)
    {
        var focus = GetFocus();
        if (focus != 0 && (focus == top || IsChild(top, focus)))
        {
            return focus;
        }

        var children = new List<(nint Hwnd, string Class)>();
        EnumChildWindows(top, (hwnd, _) =>
        {
            var name = new StringBuilder(256);
            _ = GetClassNameW(hwnd, name, name.Capacity);
            children.Add((hwnd, name.ToString()));
            return true;
        }, 0);
        var input = children.FirstOrDefault(child => child.Class.Contains("InputSite", StringComparison.OrdinalIgnoreCase));
        SmokeAssert(input.Hwnd != 0,
            $"No focused input window in {top:X}: focus is {focus:X}, children [{string.Join(", ", children.Select(child => child.Class))}]");
        SetFocus(input.Hwnd);
        return input.Hwnd;
    }

    /// <summary>Waits for the button with this automation id to be enabled, and returns it.</summary>
    private static async Task<Button> SmokeRemoteEnabledButtonAsync(FrameworkElement root, string automationId)
    {
        Button? button = null;
        await SmokeRemoteWaitAsync(() => (button = SmokeFindById<Button>(root, automationId)) is { IsEnabled: true, IsLoaded: true },
            () => $"{automationId} to be enabled (found {button is not null}, enabled {button?.IsEnabled})");
        return button!;
    }

    /// <summary>
    /// Invokes the transfer button and returns the new Transfers row titled <paramref name="title"/>.
    /// </summary>
    private static async Task<TransferOperationViewModel> SmokeRemoteTransferAsync(
        TransferManagerViewModel transfers, FrameworkElement root, string buttonId, string title)
    {
        var before = transfers.Operations.ToHashSet();
        SmokeInvoke(await SmokeRemoteEnabledButtonAsync(root, buttonId));
        TransferOperationViewModel? row = null;
        await SmokeRemoteWaitAsync(
            () => (row = transfers.Operations.FirstOrDefault(operation => !before.Contains(operation) && operation.Title == title)) is not null,
            () => $"a Transfers row titled '{title}' after {buttonId}, rows [{string.Join(" | ", transfers.Operations.Select(operation => operation.Title))}]");
        return row!;
    }

    /// <summary>Answers the manager's name prompt titled <paramref name="title"/> with <paramref name="name"/>.</summary>
    private static async Task SmokeRemoteAnswerNamePromptAsync(FrameworkElement root, string title, string name)
    {
        var dialog = await SmokeOpenDialogAsync(root.XamlRoot!, title);
        SmokeRemoteControl<TextBox>(dialog, "Remote.NamePrompt").Text = name;
        SmokeInvoke(SmokeDialogButton(dialog, "PrimaryButton"));
        await SmokeRemoteWaitAsync(() => SmokeOpenDialog(root.XamlRoot!) is null, () => $"the '{title}' prompt to close");
    }

    private static T SmokeRemoteControl<T>(DependencyObject root, string automationId) where T : DependencyObject =>
        SmokeFindById<T>(root, automationId) ?? throw new InvalidOperationException($"{automationId} ({typeof(T).Name}) is missing");

    private static Button SmokeDialogButton(ContentDialog dialog, string name) =>
        SmokeDescendants<Button>(dialog).FirstOrDefault(button => button.Name == name)
            ?? throw new InvalidOperationException($"The dialog '{dialog.Title}' has no {name}");

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

    /// <summary>The generic <c>SumaFile.Remote.*</c> targets in the user's Credential Manager.</summary>
    private static string[] SmokeRemoteCredentialTargets()
    {
        const int CRED_TYPE_GENERIC = 1;
        const int ERROR_NOT_FOUND = 1168;
        if (!CredEnumerateW(SmokeRemoteCredentialPrefix + "*", 0, out var count, out var credentials))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ERROR_NOT_FOUND ? [] : throw new InvalidOperationException($"CredEnumerateW failed: {error}");
        }

        try
        {
            var targets = new List<string>();
            for (var index = 0; index < count; index++)
            {
                // CREDENTIALW: DWORD Flags; DWORD Type; LPWSTR TargetName; ...
                var credential = Marshal.ReadIntPtr(credentials, index * IntPtr.Size);
                if (Marshal.ReadInt32(credential, 4) == CRED_TYPE_GENERIC
                    && Marshal.PtrToStringUni(Marshal.ReadIntPtr(credential, 8)) is { } target)
                {
                    targets.Add(target);
                }
            }

            return [.. targets];
        }
        finally
        {
            CredFree(credentials);
        }
    }

    /// <summary>Deletes one generic credential; a target that is already gone is fine.</summary>
    private static void SmokeDeleteRemoteCredential(string target)
    {
        const int CRED_TYPE_GENERIC = 1;
        const int ERROR_NOT_FOUND = 1168;
        if (!CredDeleteW(target, CRED_TYPE_GENERIC, 0) && Marshal.GetLastWin32Error() is var error and not ERROR_NOT_FOUND)
        {
            throw new InvalidOperationException($"CredDeleteW({target}) failed: {error}");
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerateW(string filter, int flags, out int count, out nint credentials);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);

    [DllImport("user32.dll")]
    private static extern nint GetFocus();

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint child);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    private delegate bool SmokeEnumWindowsProc(nint hwnd, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, SmokeEnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hwnd, StringBuilder name, int maxCount);
}
#endif
