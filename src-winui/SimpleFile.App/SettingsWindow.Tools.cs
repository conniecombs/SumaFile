using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;

namespace SimpleFile.App;

public sealed partial class SettingsWindow
{
    private const string RepositoryUrl = "https://github.com/conniecombs/SumaFile";
    private FileOperationService? _fileOps;
    private bool _checkedUpdateIsInstallable;
    private RemoteManagerWindow? _settingsRemoteManagerWindow;

    // No settings field stores this yet, so it lasts for the session (across Settings reopenings).
    private static DateTimeOffset? s_lastUpdateCheck;

    private void ShowLastUpdateCheck()
    {
        if (s_lastUpdateCheck is not { } checkedAt)
        {
            LastUpdateCheckText.Visibility = Visibility.Collapsed;
            return;
        }

        LastUpdateCheckText.Text = $"Last checked: {checkedAt.LocalDateTime:g}";
        LastUpdateCheckText.Visibility = Visibility.Visible;
    }

    private async void OnTransferOptionsClicked(object sender, RoutedEventArgs e)
    {
        TransferOptionsStatusText.Visibility = Visibility.Collapsed;
        TransferOptionsButton.IsEnabled = false;
        try
        {
            await TransferOptionsDialog.ShowDefaultsAsync(Content.XamlRoot);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            TransferOptionsStatusText.Text = exception.Message;
            TransferOptionsStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            TransferOptionsButton.IsEnabled = true;
        }
    }

    private async Task LoadVersionAsync(FileOperationService fileOps, CancellationToken cancellationToken = default)
    {
        try
        {
            var version = await fileOps.GetAppVersionAsync(cancellationToken).ConfigureAwait(true);
            CurrentVersionText.Text = $"Version {version}";
            AboutVersionText.Text = $"Version {version}";
            ShowLastUpdateCheck();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CurrentVersionText.Text = "Version unavailable";
            AboutVersionText.Text = "Version unavailable";
            UpdateStatusText.Text = $"Unable to load version: {exception.Message}";
        }
    }

    private async void OnCheckUpdatesClicked(object sender, RoutedEventArgs e)
    {
        if (_fileOps == null) return;
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        _checkedUpdateIsInstallable = false;
        UpdateStatusText.Text = "Checking for updates...";
        try
        {
            var update = await _fileOps.CheckForUpdateAsync().ConfigureAwait(true);
            if (update != null)
            {
                InstallUpdateButton.Visibility = Visibility.Visible;
                InstallUpdateButton.IsEnabled = true;
                _checkedUpdateIsInstallable = update.Installable;
                InstallUpdateButton.Content = update.Installable ? "Download and install" : "Open download page";
                var version = string.IsNullOrWhiteSpace(update.Version)
                    ? "an update"
                    : update.Version;
                UpdateStatusText.Text = update.Installable
                    ? $"Version {version} is available. SumaFile checks the signed installer before running it."
                    : $"Version {version} is available. Download it from the release page.";
            }
            else
            {
                UpdateStatusText.Text = "You have the latest version.";
            }

            s_lastUpdateCheck = DateTimeOffset.Now;
            ShowLastUpdateCheck();
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "Update check cancelled.";
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = exception.Message;
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void OnInstallUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_fileOps == null) return;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = _checkedUpdateIsInstallable
            ? "Downloading and verifying installer..."
            : "Opening the download page...";
        try
        {
            if (_checkedUpdateIsInstallable)
            {
                var progress = new Progress<long[]>(values =>
                {
                    if (values.Length >= 2)
                    {
                        var downloaded = values[0];
                        var total = values[1];
                        UpdateStatusText.Text = total > 0
                            ? $"Downloading update: {downloaded:N0} of {total:N0} bytes"
                            : $"Downloading update: {downloaded:N0} bytes";
                    }
                });
                await _fileOps.InstallUpdateAsync(progress).ConfigureAwait(true);
                UpdateStatusText.Text = "The installer is running. SumaFile may close while the update finishes.";
            }
            else
            {
                await _fileOps.OpenExternalUrlAsync(RepositoryUrl + "/releases").ConfigureAwait(true);
                UpdateStatusText.Text = "Download the latest release from the page that opened.";
            }
        }
        catch (Exception exception)
        {
            InstallUpdateButton.Content = "Open download page";
            _checkedUpdateIsInstallable = false;
            UpdateStatusText.Text = "The update couldn't be installed here. Download it from " + RepositoryUrl + "/releases instead. " + exception.Message;
        }
        finally
        {
            InstallUpdateButton.IsEnabled = true;
        }
    }

    private async void OnGitHubClicked(object sender, RoutedEventArgs e)
    {
        if (_fileOps is null)
        {
            return;
        }

        var link = sender as Control;
        if (link is not null)
        {
            link.IsEnabled = false;
        }

        AboutStatusText.Visibility = Visibility.Collapsed;
        try
        {
            await _fileOps.OpenExternalUrlAsync(RepositoryUrl).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AboutStatusText.Text = exception.Message;
            AboutStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (link is not null)
            {
                link.IsEnabled = true;
            }
        }
    }

    private void OnOpenRemoteManagerClicked(object sender, RoutedEventArgs e)
    {
        if (_fileOps is null)
        {
            ToolsStatusText.Text = "SumaFile's file service is not ready yet.";
            return;
        }

        if (_settingsRemoteManagerWindow is { IsClosed: false } existing)
        {
            existing.Activate();
            return;
        }

        _settingsRemoteManagerWindow = new RemoteManagerWindow(new RemoteManagerViewModel(_fileOps));
        _settingsRemoteManagerWindow.Closed += (closedSender, _) =>
        {
            if (ReferenceEquals(_settingsRemoteManagerWindow, closedSender))
            {
                _settingsRemoteManagerWindow = null;
            }
        };
        _settingsRemoteManagerWindow.Activate();
        ToolsStatusText.Text = "FTP/SFTP manager opened.";
    }
}
