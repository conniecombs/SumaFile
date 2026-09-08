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

    private async Task LoadVersionAsync(FileOperationService fileOps, CancellationToken cancellationToken = default)
    {
        try
        {
            var version = await fileOps.GetAppVersionAsync(cancellationToken).ConfigureAwait(true);
            CurrentVersionText.Text = $"Current Version: {version}";
            AboutVersionText.Text = $"Version {version}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CurrentVersionText.Text = "Current Version: unavailable";
            AboutVersionText.Text = "Version unavailable";
            UpdateStatusText.Text = $"Unable to load version: {exception.Message}";
        }
    }

    private async Task CheckRarInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (_fileOps == null) return;
        try
        {
            var installed = await _fileOps.CheckRarInstalledAsync(cancellationToken).ConfigureAwait(true);
            RarStatusText.Text = installed ? "Installed" : "Not installed";
            InstallRarButton.IsEnabled = !installed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RarStatusText.Text = $"Unable to check RAR support: {exception.Message}";
            InstallRarButton.IsEnabled = true;
        }
    }

    private async void OnInstallRarClicked(object sender, RoutedEventArgs e)
    {
        if (_fileOps == null) return;
        InstallRarButton.IsEnabled = false;
        RarStatusText.Text = "Preparing install...";

        try
        {
            var prepResult = await _fileOps.PrepareRarInstallAsync().ConfigureAwait(true);
            if (prepResult != null)
            {
                var dialog = new ContentDialog
                {
                    Title = "Install RAR Support",
                    Content = "This will download and install third-party components to support RAR extraction. Do you agree to their terms?",
                    PrimaryButtonText = "Install",
                    CloseButtonText = "Cancel",
                    XamlRoot = Content.XamlRoot
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    RarStatusText.Text = "Installing...";
                    await _fileOps.InstallRarAsync(prepResult.ConfirmationToken).ConfigureAwait(true);
                    await CheckRarInstalledAsync().ConfigureAwait(true);
                }
                else
                {
                    await _fileOps.DiscardRarInstallAsync(prepResult.ConfirmationToken).ConfigureAwait(true);
                    await CheckRarInstalledAsync().ConfigureAwait(true);
                }
            }
            else
            {
                RarStatusText.Text = "Failed to prepare installation.";
                InstallRarButton.IsEnabled = true;
            }
        }
        catch (OperationCanceledException)
        {
            RarStatusText.Text = "Installation cancelled.";
            InstallRarButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            RarStatusText.Text = exception.Message;
            InstallRarButton.IsEnabled = true;
        }
    }

    private async void OnCheckUpdatesClicked(object sender, RoutedEventArgs e)
    {
        if (_fileOps == null) return;
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        _checkedUpdateIsInstallable = false;
        UpdateStatusText.Text = "Checking...";
        try
        {
            var update = await _fileOps.CheckForUpdateAsync().ConfigureAwait(true);
            if (update != null)
            {
                InstallUpdateButton.Visibility = Visibility.Visible;
                InstallUpdateButton.IsEnabled = true;
                _checkedUpdateIsInstallable = update.Installable;
                InstallUpdateButton.Content = update.Installable ? "Download & Install" : "Open GitHub Releases";
                var version = string.IsNullOrWhiteSpace(update.Version)
                    ? "an update"
                    : update.Version;
                UpdateStatusText.Text = update.Installable
                    ? $"Update available: {version}. The installer metadata is signed and ready to verify before launch."
                    : $"Update available: {version}. This build cannot verify the installer metadata, so download it from GitHub Releases.";
            }
            else
            {
                UpdateStatusText.Text = "No updates available.";
            }
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
            : "Opening GitHub Releases...";
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
                UpdateStatusText.Text = "Verified installer launched. SumaFile may close while the update finishes.";
            }
            else
            {
                await _fileOps.OpenExternalUrlAsync(RepositoryUrl + "/releases").ConfigureAwait(true);
                UpdateStatusText.Text = "Download the latest release from the GitHub page.";
            }
        }
        catch (Exception exception)
        {
            InstallUpdateButton.Content = "Open GitHub Releases";
            _checkedUpdateIsInstallable = false;
            UpdateStatusText.Text = "Could not install in-app. Please visit " + RepositoryUrl + "/releases to download the update. " + exception.Message;
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
}
