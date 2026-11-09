using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SimpleFile.PluginSdk;
using SumaFile.Remote.Model;
using Windows.Storage.Pickers;
using Windows.System;

namespace SumaFile.Remote.Views;

/// <summary>
/// The FTP/SFTP manager: a plain <see cref="Window"/> subclass built in code (spec §5.3), ported from the 1.0.4
/// <c>RemoteManagerWindow.xaml</c>. The layout is one theme-aware <see cref="Xaml"/> fragment; the code wires its
/// events, fills its lists with code-created items and copies view model values on <c>PropertyChanged</c>. Every
/// dialog goes through the host's dialog gate on this window's own XamlRoot, and the host gives the window its
/// chrome and closes it before the plugin stops.
/// </summary>
internal sealed partial class RemoteManagerWindow : Window
{
    internal const string WindowTitle = "FTP/SFTP manager";

    private const string Layout = """
        <Grid MinWidth="1120" MinHeight="520" Padding="16" RowSpacing="12" Background="{ThemeResource SfBgSecondaryBrush}">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>

            <Grid ColumnSpacing="12">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <StackPanel Spacing="3">
                    <TextBlock FontSize="18" FontWeight="SemiBold" Foreground="{ThemeResource SfTextPrimaryBrush}" Text="FTP/SFTP manager" />
                    <TextBlock x:Name="SummaryText" FontSize="12" Foreground="{ThemeResource SfTextMutedBrush}" TextTrimming="CharacterEllipsis" />
                </StackPanel>
                <Button x:Name="RefreshButton" Grid.Column="1" Style="{ThemeResource SfGhostButtonStyle}" ToolTipService.ToolTip="Refresh remote profiles" AutomationProperties.Name="Refresh remote profiles">
                    <StackPanel Orientation="Horizontal" Spacing="6">
                        <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE72C;" />
                        <TextBlock Text="Refresh" />
                    </StackPanel>
                </Button>
                <Button x:Name="ConnectButton" Grid.Column="2" Style="{ThemeResource SfGhostButtonStyle}" ToolTipService.ToolTip="Connect to selected profile" AutomationProperties.Name="Connect to selected profile">
                    <StackPanel Orientation="Horizontal" Spacing="6">
                        <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE8CE;" />
                        <TextBlock Text="Connect" />
                    </StackPanel>
                </Button>
                <Button x:Name="DisconnectButton" Grid.Column="3" Style="{ThemeResource SfGhostButtonStyle}" ToolTipService.ToolTip="Disconnect current remote session" AutomationProperties.Name="Disconnect current remote session">
                    <StackPanel Orientation="Horizontal" Spacing="6">
                        <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE711;" />
                        <TextBlock Text="Disconnect" />
                    </StackPanel>
                </Button>
            </Grid>

            <Grid Grid.Row="1" ColumnSpacing="12">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="260" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>

                <Border Padding="12" Background="{ThemeResource SfBgPrimaryBrush}" BorderBrush="{ThemeResource SfBorderBrush}" BorderThickness="1" CornerRadius="8">
                    <Grid RowSpacing="10">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="*" />
                        </Grid.RowDefinitions>
                        <Grid ColumnSpacing="6">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <TextBlock VerticalAlignment="Center" FontSize="13" FontWeight="SemiBold" Foreground="{ThemeResource SfTextPrimaryBrush}" Text="Profiles" />
                            <Button x:Name="NewProfileButton" Grid.Column="1" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="New remote profile" ToolTipService.ToolTip="New remote profile">
                                <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE710;" />
                            </Button>
                            <Button x:Name="EditProfileButton" Grid.Column="2" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Edit selected remote profile" ToolTipService.ToolTip="Edit selected remote profile">
                                <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE70F;" />
                            </Button>
                            <Button x:Name="DeleteProfileButton" Grid.Column="3" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Delete selected remote profile" ToolTipService.ToolTip="Delete selected remote profile">
                                <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE74D;" />
                            </Button>
                        </Grid>
                        <ListView x:Name="ProfileListView" Grid.Row="1" SelectionMode="Single" AutomationProperties.Name="Remote profiles" />
                    </Grid>
                </Border>

                <Border Grid.Column="1" Padding="12" Background="{ThemeResource SfBgPrimaryBrush}" BorderBrush="{ThemeResource SfBorderBrush}" BorderThickness="1" CornerRadius="8">
                    <Grid RowSpacing="10">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="*" />
                        </Grid.RowDefinitions>
                        <Grid ColumnSpacing="8">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <TextBox x:Name="LocalPathBox" PlaceholderText="Choose or type a local folder" Style="{ThemeResource SfTextBoxStyle}" AutomationProperties.Name="Local folder" />
                            <Button x:Name="LocalGoButton" Grid.Column="1" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Go to the typed local folder" ToolTipService.ToolTip="Go to the typed local folder">
                                <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE72A;" />
                            </Button>
                            <Button x:Name="LocalUpButton" Grid.Column="2" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Go to parent local folder" ToolTipService.ToolTip="Go to parent local folder">
                                <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE74A;" />
                            </Button>
                            <Button x:Name="ChooseLocalButton" Grid.Column="3" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Choose local folder" ToolTipService.ToolTip="Choose local folder">
                                <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE8B7;" />
                            </Button>
                            <Button x:Name="UploadButton" Grid.Column="4" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Upload selected local files to remote pane" ToolTipService.ToolTip="Upload selected local files to remote pane">
                                <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE898;" />
                            </Button>
                            <Button x:Name="LocalRefreshButton" Grid.Column="5" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Refresh local folder" ToolTipService.ToolTip="Refresh local folder">
                                <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE72C;" />
                            </Button>
                        </Grid>
                        <ListView x:Name="LocalListView" Grid.Row="1" IsItemClickEnabled="True" SelectionMode="Extended" AutomationProperties.Name="Local files" />
                    </Grid>
                </Border>

                <Border Grid.Column="2" Padding="12" Background="{ThemeResource SfBgPrimaryBrush}" BorderBrush="{ThemeResource SfBorderBrush}" BorderThickness="1" CornerRadius="8">
                    <Grid RowSpacing="10">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="*" />
                            <RowDefinition Height="Auto" />
                        </Grid.RowDefinitions>
                        <Grid ColumnSpacing="8">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <TextBox x:Name="RemotePathBox" IsReadOnly="True" Style="{ThemeResource SfTextBoxStyle}" AutomationProperties.Name="Remote folder" />
                            <Button x:Name="RemoteUpButton" Grid.Column="1" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Go to parent remote folder" ToolTipService.ToolTip="Go to parent remote folder">
                                <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE74A;" />
                            </Button>
                            <Button x:Name="NewFolderButton" Grid.Column="2" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Create remote folder" ToolTipService.ToolTip="Create remote folder">
                                <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE8F4;" />
                            </Button>
                            <Button x:Name="RemoteUploadButton" Grid.Column="3" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Upload selected local files to remote pane" ToolTipService.ToolTip="Upload selected local files to remote pane">
                                <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE898;" />
                            </Button>
                            <Button x:Name="DownloadButton" Grid.Column="4" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Download selected remote files and folders to local pane" ToolTipService.ToolTip="Download selected remote files and folders to local pane">
                                <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE896;" />
                            </Button>
                            <Button x:Name="RenameButton" Grid.Column="5" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Rename selected remote item" ToolTipService.ToolTip="Rename selected remote item">
                                <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE8AC;" />
                            </Button>
                            <Button x:Name="DeleteEntriesButton" Grid.Column="6" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Delete selected remote items" ToolTipService.ToolTip="Delete selected remote items">
                                <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE74D;" />
                            </Button>
                            <Button x:Name="RemoteRefreshButton" Grid.Column="7" Style="{ThemeResource SfGhostButtonStyle}" ToolTipService.ToolTip="Refresh remote directory" AutomationProperties.Name="Refresh remote directory">
                                <StackPanel Orientation="Horizontal" Spacing="6">
                                    <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="11" Glyph="&#xE72C;" />
                                    <TextBlock Text="Refresh" />
                                </StackPanel>
                            </Button>
                        </Grid>
                        <ListView x:Name="RemoteListView" Grid.Row="1" IsItemClickEnabled="True" SelectionMode="Extended" AutomationProperties.Name="Remote files" />
                        <ListView x:Name="TransferListView" Grid.Row="2" MaxHeight="112" SelectionMode="None" AutomationProperties.Name="Finished transfers" />
                    </Grid>
                </Border>
            </Grid>

            <StackPanel Grid.Row="2" Spacing="6">
                <StackPanel x:Name="TransferBar" Orientation="Horizontal" Spacing="8">
                    <ComboBox x:Name="ConflictBox" AutomationProperties.Name="Existing destination files" ToolTipService.ToolTip="Existing folders are merged. This policy applies to files with matching names." />
                    <Button x:Name="CancelTransferButton" />
                    <Button x:Name="RetryTransferButton" />
                    <Button x:Name="TransferOptionsButton" />
                </StackPanel>
                <TextBlock x:Name="ProgressText" FontSize="12" Foreground="{ThemeResource SfTextMutedBrush}" TextTrimming="CharacterEllipsis" />
                <TextBlock x:Name="TuningText" FontSize="12" Foreground="{ThemeResource SfTextMutedBrush}" TextWrapping="Wrap" />
                <TextBlock x:Name="StatusTextBlock" FontSize="12" Foreground="{ThemeResource SfTextMutedBrush}" TextTrimming="CharacterEllipsis" />
            </StackPanel>
        </Grid>
        """;

    private readonly IPluginHost _host;
    private readonly RemoteManagerViewModel _viewModel;
    private readonly FrameworkElement _root;
    private readonly TextBlock _summaryText;
    private readonly Button _refreshButton;
    private readonly Button _connectButton;
    private readonly Button _disconnectButton;
    private readonly Button _newProfileButton;
    private readonly Button _editProfileButton;
    private readonly Button _deleteProfileButton;
    private readonly TextBox _localPathBox;
    private readonly Button _localGoButton;
    private readonly Button _uploadButton;
    private readonly Button _remoteUploadButton;
    private readonly TextBox _remotePathBox;
    private readonly Button _remoteUpButton;
    private readonly Button _newFolderButton;
    private readonly Button _downloadButton;
    private readonly Button _renameButton;
    private readonly Button _deleteEntriesButton;
    private readonly Button _remoteRefreshButton;
    private readonly ComboBox _conflictBox;
    private readonly Button _cancelTransferButton;
    private readonly Button _retryTransferButton;
    private readonly TextBlock _progressText;
    private readonly TextBlock _tuningText;
    private readonly TextBlock _statusText;
    private string _localPathShown = "";
    private bool _loaded;

    public RemoteManagerWindow(IPluginHost host, RemoteManagerViewModel viewModel)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Title = WindowTitle;

        _root = Xaml.Load<FrameworkElement>(Layout);
        _summaryText = Xaml.Find<TextBlock>(_root, "SummaryText");
        _refreshButton = Xaml.Find<Button>(_root, "RefreshButton", RemoteAutomationIds.Refresh);
        _connectButton = Xaml.Find<Button>(_root, "ConnectButton", RemoteAutomationIds.Connect);
        _disconnectButton = Xaml.Find<Button>(_root, "DisconnectButton", RemoteAutomationIds.Disconnect);
        _newProfileButton = Xaml.Find<Button>(_root, "NewProfileButton", RemoteAutomationIds.NewProfile);
        _editProfileButton = Xaml.Find<Button>(_root, "EditProfileButton", RemoteAutomationIds.EditProfile);
        _deleteProfileButton = Xaml.Find<Button>(_root, "DeleteProfileButton", RemoteAutomationIds.DeleteProfile);
        _profileList = Xaml.Find<ListView>(_root, "ProfileListView", RemoteAutomationIds.ProfileList);
        _localPathBox = Xaml.Find<TextBox>(_root, "LocalPathBox", RemoteAutomationIds.LocalPath);
        _localGoButton = Xaml.Find<Button>(_root, "LocalGoButton", RemoteAutomationIds.LocalGo);
        _uploadButton = Xaml.Find<Button>(_root, "UploadButton", RemoteAutomationIds.Upload);
        _localList = Xaml.Find<ListView>(_root, "LocalListView", RemoteAutomationIds.LocalList);
        _remotePathBox = Xaml.Find<TextBox>(_root, "RemotePathBox", RemoteAutomationIds.RemotePath);
        _remoteUpButton = Xaml.Find<Button>(_root, "RemoteUpButton");
        _newFolderButton = Xaml.Find<Button>(_root, "NewFolderButton", RemoteAutomationIds.NewFolder);
        _remoteUploadButton = Xaml.Find<Button>(_root, "RemoteUploadButton");
        _downloadButton = Xaml.Find<Button>(_root, "DownloadButton", RemoteAutomationIds.Download);
        _renameButton = Xaml.Find<Button>(_root, "RenameButton", RemoteAutomationIds.Rename);
        _deleteEntriesButton = Xaml.Find<Button>(_root, "DeleteEntriesButton", RemoteAutomationIds.DeleteEntries);
        _remoteRefreshButton = Xaml.Find<Button>(_root, "RemoteRefreshButton");
        _remoteList = Xaml.Find<ListView>(_root, "RemoteListView", RemoteAutomationIds.RemoteList);
        _transferList = Xaml.Find<ListView>(_root, "TransferListView", RemoteAutomationIds.TransferList);
        _conflictBox = Xaml.Find<ComboBox>(_root, "ConflictBox", RemoteAutomationIds.Conflict);
        _cancelTransferButton = Xaml.Find<Button>(_root, "CancelTransferButton", RemoteAutomationIds.CancelTransfer);
        _retryTransferButton = Xaml.Find<Button>(_root, "RetryTransferButton", RemoteAutomationIds.RetryTransfer);
        var transferOptionsButton = Xaml.Find<Button>(_root, "TransferOptionsButton", RemoteAutomationIds.TransferOptions);
        _progressText = Xaml.Find<TextBlock>(_root, "ProgressText");
        _tuningText = Xaml.Find<TextBlock>(_root, "TuningText");
        _statusText = Xaml.Find<TextBlock>(_root, "StatusTextBlock", RemoteAutomationIds.Status);

        // The conflict choices and the transfer buttons keep the 1.0.4 wording.
        _conflictBox.Items.Add(new ComboBoxItem { Content = "Existing files: report conflict", Tag = "error" });
        _conflictBox.Items.Add(new ComboBoxItem { Content = "Existing files: skip", Tag = "skip" });
        _conflictBox.Items.Add(new ComboBoxItem { Content = "Replace (keeps a backup until the transfer finishes)", Tag = "replace" });
        _conflictBox.Items.Add(new ComboBoxItem { Content = "Existing files: keep both", Tag = "keep-both" });
        _conflictBox.SelectedIndex = 0;
        _cancelTransferButton.Content = "Cancel transfer";
        _retryTransferButton.Content = "Retry incomplete";
        transferOptionsButton.Content = "Transfer options...";

        _refreshButton.Click += (_, _) => _ = RunRemoteActionAsync(() => _viewModel.LoadProfilesAsync());
        _connectButton.Click += (_, _) => _ = ConnectAsync();
        _disconnectButton.Click += (_, _) => _ = RunRemoteActionAsync(() => _viewModel.DisconnectAsync());
        _newProfileButton.Click += (_, _) => _ = EditProfileAsync(null);
        _editProfileButton.Click += (_, _) => _ = EditProfileAsync(_viewModel.SelectedProfile);
        _deleteProfileButton.Click += (_, _) => _ = DeleteProfileAsync();
        _localGoButton.Click += (_, _) => _ = GoToTypedLocalPathAsync();
        _localPathBox.KeyDown += OnLocalPathKeyDown;
        Xaml.Find<Button>(_root, "LocalUpButton").Click += (_, _) => _ = RunRemoteActionAsync(() => _viewModel.GoToParentLocalDirectoryAsync());
        Xaml.Find<Button>(_root, "ChooseLocalButton").Click += (_, _) => _ = ChooseLocalFolderAsync();
        Xaml.Find<Button>(_root, "LocalRefreshButton").Click += (_, _) => _ = RunRemoteActionAsync(() => _viewModel.RefreshLocalDirectoryAsync());
        _uploadButton.Click += (_, _) => _ = UploadSelectedLocalEntriesAsync();
        _remoteUploadButton.Click += (_, _) => _ = UploadSelectedLocalEntriesAsync();
        _remoteUpButton.Click += (_, _) => _ = RunRemoteActionAsync(() => _viewModel.GoToParentRemoteDirectoryAsync());
        _newFolderButton.Click += (_, _) => _ = CreateRemoteDirectoryAsync();
        _downloadButton.Click += (_, _) => _ = RunRemoteActionAsync(() => _viewModel.DownloadRemoteEntriesToLocalAsync(SelectedRemoteEntries()));
        _renameButton.Click += (_, _) => _ = RenameRemoteEntryAsync();
        _deleteEntriesButton.Click += (_, _) => _ = DeleteRemoteEntriesAsync();
        _remoteRefreshButton.Click += (_, _) => _ = RunRemoteActionAsync(() => _viewModel.RefreshRemoteDirectoryAsync());
        _cancelTransferButton.Click += (_, _) => _viewModel.CancelTransfer();
        _retryTransferButton.Click += (_, _) => _ = RunRemoteActionAsync(() => _viewModel.RetryTransfersAsync());
        transferOptionsButton.Click += (_, _) => _ = RemoteTransferOptionsDialog.ShowAsync(_host, _root.XamlRoot);
        _conflictBox.SelectionChanged += (_, _) =>
        {
            if (_conflictBox.SelectedItem is ComboBoxItem { Tag: string policy })
            {
                _viewModel.ConflictAction = policy;
            }
        };

        AttachLists();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyViewModelState();

        Content = _root;
        _root.Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public bool IsClosed { get; private set; }

    /// <summary>
    /// Hands the window to the host, which gives it SumaFile's chrome and tracks it, then shows it. A host that is
    /// already stopping the plugin closes the window at once, and it is then not shown.
    /// </summary>
    public void AttachAndShow()
    {
        _host.Windows.Attach(this, new PluginWindowOptions("FTP/SFTP manager", 1180, 680));
        if (!IsClosed)
        {
            Activate();
        }
    }

    /// <summary>Opens the window on <paramref name="profileId"/> (from the sidebar), unless a session is open.</summary>
    public void RequestProfileSelection(string? profileId)
    {
        if (_viewModel.IsConnected)
        {
            return;
        }

        _viewModel.RequestProfileSelection(profileId);
        if (_loaded)
        {
            _ = RunRemoteActionAsync(() => _viewModel.LoadProfilesAsync());
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await RunRemoteActionAsync(() => _viewModel.LoadProfilesAsync());
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        IsClosed = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        DetachLists();
        _viewModel.CancelTransfer();
        if (_viewModel.IsConnected)
        {
            // The session lives in the backend until it exits; end it with the window, as nothing can reach it after.
            _ = DisconnectQuietlyAsync();
        }
    }

    private async Task DisconnectQuietlyAsync()
    {
        try
        {
            await _viewModel.DisconnectAsync();
        }
        catch (Exception exception)
        {
            _host.Logger.Info("The closed manager's session did not disconnect: " + exception.Message);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsClosed)
        {
            ApplyViewModelState();
        }
    }

    /// <summary>Copies every view model value the window shows; cheap enough to run on each change.</summary>
    private void ApplyViewModelState()
    {
        _summaryText.Text = _viewModel.SelectedProfileSummary;
        _connectButton.IsEnabled = _viewModel.CanConnect;
        _disconnectButton.IsEnabled = _viewModel.CanDisconnect;
        _newProfileButton.IsEnabled = _viewModel.CanCreateProfile;
        _editProfileButton.IsEnabled = _viewModel.CanEditProfile;
        _deleteProfileButton.IsEnabled = _viewModel.CanDeleteProfile;
        _uploadButton.IsEnabled = _viewModel.CanUseWorkspaceTransfers;
        _remoteUploadButton.IsEnabled = _viewModel.CanUseWorkspaceTransfers;
        _downloadButton.IsEnabled = _viewModel.CanUseWorkspaceTransfers;
        _remoteUpButton.IsEnabled = _viewModel.CanMutateRemoteEntries;
        _newFolderButton.IsEnabled = _viewModel.CanMutateRemoteEntries;
        _renameButton.IsEnabled = _viewModel.CanMutateRemoteEntries;
        _deleteEntriesButton.IsEnabled = _viewModel.CanMutateRemoteEntries;
        _remoteRefreshButton.IsEnabled = _viewModel.CanDisconnect;
        _cancelTransferButton.IsEnabled = _viewModel.CanCancelTransfer;
        _retryTransferButton.IsEnabled = _viewModel.CanRetryTransfers;
        _remotePathBox.Text = _viewModel.RemotePath;
        if (!string.Equals(_localPathShown, _viewModel.LocalPath, StringComparison.Ordinal))
        {
            // The box is editable: replace what the user typed only when the local folder itself changed.
            _localPathBox.Text = _viewModel.LocalPath;
            _localPathShown = _viewModel.LocalPath;
        }

        _progressText.Text = _viewModel.TransferProgressText;
        _tuningText.Text = _viewModel.TransferTuningText;
        _statusText.Text = _viewModel.StatusText;
        SyncSelectedProfile();
    }

    private async Task ConnectAsync() =>
        await RunRemoteActionAsync(async () =>
        {
            var outcome = await RunWithProgressAsync(
                "Connecting",
                $"Connecting to {_viewModel.SelectedProfile?.Name ?? "the selected profile"}…",
                token => _viewModel.ConnectSelectedProfileAsync(null, token));
            await ResolveHostKeyAsync(outcome);
        });

    /// <summary>
    /// Asks about an untrusted or changed SFTP host key and, on consent, saves the fingerprint and reconnects.
    /// </summary>
    private async Task ResolveHostKeyAsync(ConnectOutcome outcome)
    {
        var profile = _viewModel.SelectedProfile;
        if (outcome.Connected || !outcome.IsHostKeyProblem || profile is null || IsClosed)
        {
            return;
        }

        var fingerprint = await RemoteHostKeyDialog.AskAsync(_host, _root.XamlRoot, profile, outcome);
        if (fingerprint is null || IsClosed)
        {
            return;
        }

        var retry = await RunWithProgressAsync(
            "Connecting",
            $"Connecting to {profile.Name}…",
            token => _viewModel.TrustFingerprintAndConnectAsync(fingerprint, token));
        if (!retry.Connected && retry.IsHostKeyProblem && !IsClosed)
        {
            await ShowErrorAsync(retry.Error ?? "The server's key could not be verified.");
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> under a progress <see cref="ContentDialog"/> on this window. The window hides the
    /// dialog when the work ends; the dialog's Cancel cancels the work.
    /// </summary>
    private async Task<T> RunWithProgressAsync<T>(string title, string detail, Func<CancellationToken, Task<T>> work)
    {
        using var cancellation = new CancellationTokenSource();
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = title,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new ProgressRing { IsActive = true, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Left },
                    new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                },
            },
            CloseButtonText = "Cancel",
        };
        var completed = false;
        var workTask = work(cancellation.Token);
        dialog.Opened += (_, _) =>
        {
            if (completed)
            {
                dialog.Hide();
            }
        };
        _ = workTask.ContinueWith(
            _ => dialog.DispatcherQueue.TryEnqueue(() =>
            {
                completed = true;
                dialog.Hide();
            }),
            TaskScheduler.Default);

        if (!workTask.IsCompleted)
        {
            await _host.Dialogs.ShowDialogAsync(dialog, CancellationToken.None);
        }

        if (!workTask.IsCompleted)
        {
            // The user pressed Cancel before the work finished.
            cancellation.Cancel();
        }

        return await workTask;
    }

    private async Task EditProfileAsync(RemoteProfile? profile)
    {
        if (IsClosed)
        {
            return;
        }

        RemoteProfileDialogResult? result;
        try
        {
            result = await RemoteProfileDialog.ShowAsync(
                _host,
                _root.XamlRoot,
                profile,
                (input, secret) => _viewModel.TestProfileAsync(input, secret),
                PickPrivateKeyFileAsync);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(exception.Message);
            return;
        }

        if (result is not null)
        {
            await RunRemoteActionAsync(() => _viewModel.SaveProfileAsync(result.Profile, result.Secret));
        }
    }

    private async Task DeleteProfileAsync()
    {
        var profile = _viewModel.SelectedProfile;
        if (profile is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = "Delete remote profile",
            Content = $"Delete {profile.Name}?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await _host.Dialogs.ShowDialogAsync(dialog, CancellationToken.None) != PluginDialogResult.Primary)
        {
            return;
        }

        await RunRemoteActionAsync(() => _viewModel.DeleteSelectedProfileAsync());
    }

    private async Task CreateRemoteDirectoryAsync()
    {
        var name = await RemoteNameDialog.ShowAsync(_host, _root.XamlRoot, "New remote folder", "Create");
        if (name is null)
        {
            return;
        }

        await RunRemoteActionAsync(() => _viewModel.CreateRemoteDirectoryAsync(name));
    }

    private async Task RenameRemoteEntryAsync()
    {
        var entry = SelectedRemoteEntries().FirstOrDefault();
        if (entry is null)
        {
            await RunRemoteActionAsync(() => _viewModel.RenameRemoteEntryAsync(null, ""));
            return;
        }

        var newName = await RemoteNameDialog.ShowAsync(_host, _root.XamlRoot, "Rename remote item", "Rename", entry.Name);
        if (newName is null)
        {
            return;
        }

        await RunRemoteActionAsync(() => _viewModel.RenameRemoteEntryAsync(entry, newName));
    }

    private async Task DeleteRemoteEntriesAsync()
    {
        var entries = SelectedRemoteEntries();
        if (entries.Count == 0)
        {
            await RunRemoteActionAsync(() => _viewModel.DeleteRemoteEntriesAsync(entries));
            return;
        }

        var count = entries.Count;
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = count == 1 ? "Delete remote item" : "Delete remote items",
            Content = count == 1 ? "Delete the selected remote item?" : $"Delete {count} selected remote items?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await _host.Dialogs.ShowDialogAsync(dialog, CancellationToken.None) != PluginDialogResult.Primary)
        {
            return;
        }

        await RunRemoteActionAsync(() => _viewModel.DeleteRemoteEntriesAsync(entries));
    }

    private Task UploadSelectedLocalEntriesAsync() =>
        RunRemoteActionAsync(() => _viewModel.UploadLocalEntriesToRemoteAsync(SelectedLocalEntries()));

    private async Task ChooseLocalFolderAsync()
    {
        string? folder;
        try
        {
            folder = await PickLocalFolderAsync();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(exception.Message);
            return;
        }

        if (folder is not null)
        {
            await RunRemoteActionAsync(() => _viewModel.NavigateLocalPathAsync(folder));
        }
    }

    private void OnLocalPathKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = GoToTypedLocalPathAsync();
        }
    }

    /// <summary>Navigates the local pane to the folder typed into <see cref="RemoteAutomationIds.LocalPath"/>.</summary>
    private Task GoToTypedLocalPathAsync()
    {
        var path = _localPathBox.Text.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(path)
            ? Task.CompletedTask
            : RunRemoteActionAsync(async () =>
            {
                await _viewModel.NavigateLocalPathAsync(path);
                // Show the folder the view model settled on, even when it equals the last one shown.
                _localPathBox.Text = _viewModel.LocalPath;
                _localPathShown = _viewModel.LocalPath;
            });
    }

    private async Task<string?> PickLocalFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async Task<string?> PickPrivateKeyFileAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>Runs a view model action; a failure shows the manager's error dialog unless the window closed.</summary>
    private async Task RunRemoteActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!IsClosed)
            {
                await ShowErrorAsync(exception.Message);
            }
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = _root.XamlRoot,
                Title = "FTP/SFTP manager",
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 460 },
                CloseButtonText = "OK",
            };
            await _host.Dialogs.ShowDialogAsync(dialog, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // A dialog that cannot open (the window is closing) must not crash the async void handlers above.
            _host.Logger.Warn("The FTP/SFTP manager could not show an error (" + message + "): " + exception.Message);
        }
    }
}
