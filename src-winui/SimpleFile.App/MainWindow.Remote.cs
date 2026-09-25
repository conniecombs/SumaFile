using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private void ShowRemoteManagerWindow(string? profileId = null)
    {
        if (_workspace?.FileOps is null)
        {
            ShowMessage("FTP/SFTP manager", "The backend service is not ready yet.", InfoBarSeverity.Warning);
            return;
        }

        if (_remoteManagerWindow is { IsClosed: false } existing)
        {
            existing.RequestProfileSelection(profileId);
            existing.Activate();
            return;
        }

        var viewModel = new RemoteManagerViewModel(_workspace.FileOps);
        viewModel.RequestProfileSelection(profileId);
        _remoteManagerWindow = new RemoteManagerWindow(viewModel);
        _remoteManagerWindow.Closed += OnRemoteManagerWindowClosed;
        _remoteManagerWindow.Activate();
    }

    private void OnRemoteManagerWindowClosed(object sender, WindowEventArgs args)
    {
        if (ReferenceEquals(_remoteManagerWindow, sender))
        {
            _remoteManagerWindow = null;
        }

        _ = RefreshRemoteProfilesForSidebarAsync();
    }

    private async Task RefreshRemoteProfilesForSidebarAsync(CancellationToken ct = default)
    {
        if (_workspace?.FileOps is null)
        {
            RemoteProfiles.Clear();
            UpdateSidebarEmptyStates();
            ApplySidebarSectionVisibility();
            return;
        }

        try
        {
            var profiles = await _workspace.FileOps.RemoteListProfilesAsync(ct);
            var rows = profiles
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(profile => profile.Host, StringComparer.OrdinalIgnoreCase)
                .Select(RemoteProfileSidebarRow.From)
                .ToList();
            ReplaceIfChanged(RemoteProfiles, rows, SameRemoteProfileSidebarRow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            RemoteProfiles.Clear();
        }

        UpdateSidebarEmptyStates();
        ApplySidebarSectionVisibility();
    }

    private static bool SameRemoteProfileSidebarRow(RemoteProfileSidebarRow left, RemoteProfileSidebarRow right) =>
        left.ProfileId == right.ProfileId
        && left.Name == right.Name
        && left.Protocol == right.Protocol
        && left.Host == right.Host
        && left.Description == right.Description;

    private void CloseRemoteManagerWindow()
    {
        var window = _remoteManagerWindow;
        if (window is null)
        {
            return;
        }

        if (!window.IsClosed)
        {
            window.Close();
        }

        _remoteManagerWindow = null;
    }
}
