using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace SimpleFile.App;

public sealed partial class RemoteManagerWindow : Window
{
    private readonly RemoteManagerViewModel _viewModel;
    private bool _loaded;

    public RemoteManagerWindow(RemoteManagerViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        // The root grid declares MinWidth 1120 plus 16 px padding each side.
        SecondaryWindowChrome.Apply(this, "FTP/SFTP Manager", 1180, 680);

        Root.DataContext = _viewModel;
        Root.Loaded += OnLoaded;
        Closed += (_, _) => { _viewModel.CancelTransfer(); IsClosed = true; };
    }

    public bool IsClosed { get; private set; }

    /// <summary>x:Bind helper for the profile list's "Insecure" badge.</summary>
    public static Visibility InsecureBadgeVisibility(string protocol, bool insecurePlainFtp) =>
        RemoteManagerViewModel.IsInsecureProfile(protocol, insecurePlainFtp)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private async void OnTransferDefaultsClicked(object sender, RoutedEventArgs e) =>
        await TransferOptionsDialog.ShowAsync(Root.XamlRoot, _viewModel.TransferSettingsBackend, remote: true);

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

    private async void OnRefreshProfilesClicked(object sender, RoutedEventArgs e) =>
        await RunRemoteActionAsync(() => _viewModel.LoadProfilesAsync());

    private async void OnConnectClicked(object sender, RoutedEventArgs e) =>
        await RunRemoteActionAsync(async () =>
        {
            var outcome = await DialogHost.RunWithProgressAsync(
                Root.XamlRoot,
                "Connecting",
                $"Connecting to {_viewModel.SelectedProfile?.Name ?? "the selected profile"}…",
                (_, token) => _viewModel.ConnectSelectedProfileAsync(null, token));
            await ResolveHostKeyAsync(outcome);
        });

    /// <summary>
    /// Asks the user about an untrusted or changed SFTP host key and, on consent, saves the
    /// fingerprint and reconnects. A changed key defaults to Cancel.
    /// </summary>
    private async Task ResolveHostKeyAsync(ConnectOutcome outcome)
    {
        var profile = _viewModel.SelectedProfile;
        if (outcome.Connected || !outcome.IsHostKeyProblem || profile is null || IsClosed)
        {
            return;
        }

        ContentDialog dialog;
        string fingerprint;
        if (outcome.UntrustedFingerprint is { } untrusted)
        {
            fingerprint = untrusted;
            dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Trust this server?",
                Content = HostKeyDialogText($"{profile.Host}:{profile.Port}\nSHA256 fingerprint:\n{untrusted}"),
                PrimaryButtonText = "Trust and connect",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
        }
        else
        {
            fingerprint = outcome.MismatchActual!;
            dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Server key changed",
                Content = HostKeyDialogText(
                    $"The key for {profile.Host} does not match the trusted fingerprint.\n"
                    + $"Expected {outcome.MismatchExpected}\n"
                    + $"Received {outcome.MismatchActual}\n"
                    + "Only continue if you know why the key changed."),
                PrimaryButtonText = "Replace trusted key",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
        }

        if (await DialogHost.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var retry = await DialogHost.RunWithProgressAsync(
            Root.XamlRoot,
            "Connecting",
            $"Connecting to {profile.Name}…",
            (_, token) => _viewModel.TrustFingerprintAndConnectAsync(fingerprint, token));
        if (!retry.Connected && retry.IsHostKeyProblem && !IsClosed)
        {
            await ShowErrorAsync(retry.Error ?? "The server's key could not be verified.");
        }
    }

    private static TextBlock HostKeyDialogText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        MaxWidth = 460,
    };

    private async void OnDisconnectClicked(object sender, RoutedEventArgs e) =>
        await RunRemoteActionAsync(() => _viewModel.DisconnectAsync());

    private async void OnRefreshRemoteClicked(object sender, RoutedEventArgs e) =>
        await RunRemoteActionAsync(() => _viewModel.RefreshRemoteDirectoryAsync());

    private async void OnRefreshLocalClicked(object sender, RoutedEventArgs e) =>
        await RunRemoteActionAsync(() => _viewModel.RefreshLocalDirectoryAsync());

    private async void OnRemoteUpClicked(object sender, RoutedEventArgs e) =>
        await RunRemoteActionAsync(() => _viewModel.GoToParentRemoteDirectoryAsync());

    private async void OnLocalUpClicked(object sender, RoutedEventArgs e) =>
        await RunRemoteActionAsync(() => _viewModel.GoToParentLocalDirectoryAsync());

    private async void OnRemoteEntryItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FileEntry entry && entry.IsDir)
        {
            await RunRemoteActionAsync(() => _viewModel.NavigateRemoteEntryAsync(entry));
        }
    }

    private async void OnLocalEntryItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FileEntry entry && entry.IsDir)
        {
            await RunRemoteActionAsync(() => _viewModel.NavigateLocalEntryAsync(entry));
        }
    }

    private async void OnNewProfileClicked(object sender, RoutedEventArgs e)
    {
        var result = await ShowProfileDialogAsync(null);
        if (result is null)
        {
            return;
        }

        await RunRemoteActionAsync(() => _viewModel.SaveProfileAsync(result.Profile, result.Secret));
    }

    private async void OnEditProfileClicked(object sender, RoutedEventArgs e)
    {
        var result = await ShowProfileDialogAsync(_viewModel.SelectedProfile);
        if (result is null)
        {
            return;
        }

        await RunRemoteActionAsync(() => _viewModel.SaveProfileAsync(result.Profile, result.Secret));
    }

    private async void OnDeleteProfileClicked(object sender, RoutedEventArgs e)
    {
        var profile = _viewModel.SelectedProfile;
        if (profile is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Delete remote profile",
            Content = $"Delete {profile.Name}?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await DialogHost.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        await RunRemoteActionAsync(() => _viewModel.DeleteSelectedProfileAsync());
    }

    private async void OnCreateRemoteDirectoryClicked(object sender, RoutedEventArgs e)
    {
        var name = await ShowRemoteNameDialogAsync("New remote folder", "Create");
        if (name is null)
        {
            return;
        }

        await RunRemoteActionAsync(() => _viewModel.CreateRemoteDirectoryAsync(name));
    }

    private async void OnRenameRemoteEntryClicked(object sender, RoutedEventArgs e)
    {
        var entry = RemoteEntryList.SelectedItems.OfType<FileEntry>().FirstOrDefault();
        if (entry is null)
        {
            await RunRemoteActionAsync(() => _viewModel.RenameRemoteEntryAsync(null, ""));
            return;
        }

        var newName = await ShowRemoteNameDialogAsync("Rename remote item", "Rename", entry.Name);
        if (newName is null)
        {
            return;
        }

        await RunRemoteActionAsync(() => _viewModel.RenameRemoteEntryAsync(entry, newName));
    }

    private async void OnDeleteRemoteEntriesClicked(object sender, RoutedEventArgs e)
    {
        var entries = RemoteEntryList.SelectedItems.OfType<FileEntry>().ToArray();
        if (entries.Length == 0)
        {
            await RunRemoteActionAsync(() => _viewModel.DeleteRemoteEntriesAsync(entries));
            return;
        }

        var confirmed = await ConfirmRemoteDeleteAsync(entries.Length);
        if (!confirmed)
        {
            return;
        }

        await RunRemoteActionAsync(() => _viewModel.DeleteRemoteEntriesAsync(entries));
    }

    private async void OnChooseLocalFolderClicked(object sender, RoutedEventArgs e)
    {
        var localDirectory = await PickDownloadFolderAsync();
        if (localDirectory is null)
        {
            return;
        }

        await RunRemoteActionAsync(() => _viewModel.NavigateLocalPathAsync(localDirectory));
    }

    private async void OnDownloadSelectedRemoteEntriesClicked(object sender, RoutedEventArgs e)
    {
        var entries = RemoteEntryList.SelectedItems.OfType<FileEntry>().ToArray();
        await RunRemoteActionAsync(() => _viewModel.DownloadRemoteEntriesToLocalAsync(entries));
    }

    private async void OnDownloadRemoteEntriesClicked(object sender, RoutedEventArgs e)
    {
        var entries = RemoteEntryList.SelectedItems.OfType<FileEntry>().ToArray();
        if (entries.Length == 0)
        {
            await RunRemoteActionAsync(() => _viewModel.DownloadRemoteEntriesAsync(entries, ""));
            return;
        }

        var localDirectory = await PickDownloadFolderAsync();
        if (localDirectory is null)
        {
            return;
        }

        await RunRemoteActionAsync(() => _viewModel.DownloadRemoteEntriesAsync(entries, localDirectory));
    }

    private async void OnUploadSelectedLocalEntriesClicked(object sender, RoutedEventArgs e)
    {
        var entries = LocalEntryList.SelectedItems.OfType<FileEntry>().ToArray();
        await RunRemoteActionAsync(() => _viewModel.UploadLocalEntriesToRemoteAsync(entries));
    }

    private async void OnUploadLocalFilesClicked(object sender, RoutedEventArgs e)
    {
        var localPaths = await PickUploadFilesAsync();
        if (localPaths.Length == 0)
        {
            return;
        }

        await RunRemoteActionAsync(() => _viewModel.UploadLocalFilesAsync(localPaths));
    }

    private async Task<string?> PickDownloadFolderAsync()
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

    private async Task<string[]> PickUploadFilesAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var files = await picker.PickMultipleFilesAsync();
        return files.Select(file => file.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
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

    private void OnCancelTransferClicked(object sender, RoutedEventArgs e) => _viewModel.CancelTransfer();

    private async void OnRetryTransfersClicked(object sender, RoutedEventArgs e) =>
        await RunRemoteActionAsync(() => _viewModel.RetryTransfersAsync());

    private void OnConflictPolicyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: ComboBoxItem { Tag: string policy } })
            _viewModel.ConflictAction = policy;
    }

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
            if (!IsClosed) await ShowErrorAsync(exception.Message);
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "FTP/SFTP manager",
            Content = message,
            CloseButtonText = "OK",
        };
        await DialogHost.ShowAsync(dialog);
    }

    private async Task<string?> ShowRemoteNameDialogAsync(
        string title,
        string primaryButtonText,
        string? initialValue = null)
    {
        var input = new TextBox
        {
            Text = initialValue ?? "",
            PlaceholderText = "Name",
            MinWidth = 320,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = input,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await DialogHost.ShowAsync(dialog);
        return result == ContentDialogResult.Primary ? input.Text : null;
    }

    private async Task<RemoteProfileDialogResult?> ShowProfileDialogAsync(RemoteProfile? profile)
    {
        var nameBox = new TextBox
        {
            Header = "Name",
            Text = profile?.Name ?? "",
            PlaceholderText = "Production",
        };
        var protocolBox = new ComboBox
        {
            Header = "Protocol",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new ComboBoxItem { Content = "SFTP", Tag = "sftp" },
                new ComboBoxItem { Content = "FTPS", Tag = "ftps" },
                new ComboBoxItem { Content = "FTP", Tag = "ftp" },
            },
        };
        SelectCombo(protocolBox, profile?.Protocol ?? "sftp");

        var hostBox = new TextBox
        {
            Header = "Host",
            Text = profile?.Host ?? "",
            PlaceholderText = "files.example.com",
        };
        var portBox = new NumberBox
        {
            Header = "Port",
            Minimum = 1,
            Maximum = 65535,
            Value = profile?.Port is > 0 ? profile.Port : 22,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var usernameBox = new TextBox
        {
            Header = "Username",
            Text = profile?.Username ?? "",
        };
        var rootBox = new TextBox
        {
            Header = "Root path",
            Text = profile?.RootPath ?? "/",
        };
        var authBox = new ComboBox
        {
            Header = "Authentication",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new ComboBoxItem { Content = "Password", Tag = "password" },
                new ComboBoxItem { Content = "Anonymous", Tag = "anonymous" },
                new ComboBoxItem { Content = "Agent", Tag = "agent" },
                new ComboBoxItem { Content = "Private key", Tag = "private-key" },
            },
        };
        SelectCombo(authBox, profile?.AuthKind ?? "password");

        var secretBox = new PasswordBox
        {
            Header = "Password or passphrase",
            PlaceholderText = profile is null ? "" : "Leave blank to keep saved secret",
        };
        var saveSecretBox = new CheckBox
        {
            Name = "SaveSecretCheckBox",
            Content = "Save password/passphrase in Windows Credential Manager",
            IsChecked = true,
        };
        var secretHelpBlock = new TextBlock
        {
            Foreground = App.Current.Resources["SfTextMutedBrush"] as Brush,
            FontSize = 12,
            Text = "Profile settings are saved in SumaFile. Typed secrets are saved separately in Windows Credential Manager when enabled.",
            TextWrapping = TextWrapping.Wrap,
        };
        var privateKeyPathBox = new TextBox
        {
            Header = "Private key path",
            Text = profile?.PrivateKeyPath ?? "",
            PlaceholderText = @"C:\Users\you\.ssh\id_ed25519",
        };
        var privateKeyPickerButton = new Button
        {
            Content = "Pick private key",
            HorizontalAlignment = HorizontalAlignment.Left,
            Style = App.Current.Resources["SfGhostButtonStyle"] as Style,
        };
        privateKeyPickerButton.Click += async (_, _) =>
        {
            var path = await PickPrivateKeyFileAsync();
            if (!string.IsNullOrWhiteSpace(path))
            {
                privateKeyPathBox.Text = path;
            }
        };
        var privateKeyPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                privateKeyPathBox,
                privateKeyPickerButton,
            },
        };
        var fingerprintBox = new TextBox
        {
            Header = "Trusted SFTP fingerprint",
            Text = profile?.TrustedHostFingerprint ?? "",
            PlaceholderText = "SHA256:...",
        };
        // The credential target is an implementation detail: keep its value for saving but do
        // not show an editable box; it is surfaced as a tooltip on the Authentication section.
        var credentialBox = new TextBox
        {
            Header = "Credential target",
            Text = profile?.CredentialTarget ?? "",
        };
        var passiveBox = new CheckBox
        {
            Content = "Passive mode",
            IsChecked = profile?.PassiveMode ?? true,
        };
        var insecureBox = new CheckBox
        {
            Content = PlainFtpConsentText,
            IsChecked = profile?.InsecurePlainFtp ?? false,
        };
        var captionBrush = App.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var criticalBrush)
            ? criticalBrush as Brush
            : App.Current.Resources["SfTextMutedBrush"] as Brush;
        var insecureCaption = new TextBlock
        {
            Foreground = captionBrush,
            FontSize = 12,
            Text = PlainFtpConsentRequiredText,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var sftpTrustPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                fingerprintBox,
            },
        };
        var secretPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                secretBox,
                saveSecretBox,
                secretHelpBlock,
            },
        };
        var ftpOptionsPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                passiveBox,
            },
        };
        var statusBlock = new TextBlock
        {
            Foreground = App.Current.Resources["SfTextMutedBrush"] as Brush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        var authSection = CreateProfileDialogSection(
            "Authentication",
            authBox,
            secretPanel,
            privateKeyPanel,
            sftpTrustPanel);
        if (!string.IsNullOrWhiteSpace(credentialBox.Text))
        {
            ToolTipService.SetToolTip(authSection, $"Saved in Windows Credential Manager as {credentialBox.Text}");
        }

        var form = new StackPanel
        {
            MinWidth = 460,
            Spacing = 14,
            Children =
            {
                CreateProfileDialogSection(
                    "Connection",
                    nameBox,
                    protocolBox,
                    insecureBox,
                    insecureCaption,
                    hostBox,
                    portBox,
                    usernameBox,
                    rootBox),
                authSection,
                CreateProfileDialogSection(
                    "Advanced",
                    ftpOptionsPanel),
                statusBlock,
            },
        };
        var scrollViewer = new ScrollViewer
        {
            Name = "ProfileDialogScrollViewer",
            Content = form,
            MaxHeight = 440,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = profile is null ? "New remote profile" : "Edit remote profile",
            Content = scrollViewer,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Test",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        protocolBox.SelectionChanged += (_, _) => ApplyProfileDialogVisibility(
            protocolBox,
            authBox,
            usernameBox,
            secretBox,
            secretPanel,
            privateKeyPanel,
            sftpTrustPanel,
            ftpOptionsPanel,
            insecureBox);
        authBox.SelectionChanged += (_, _) => ApplyProfileDialogVisibility(
            protocolBox,
            authBox,
            usernameBox,
            secretBox,
            secretPanel,
            privateKeyPanel,
            sftpTrustPanel,
            ftpOptionsPanel,
            insecureBox);
        ApplyProfileDialogVisibility(
            protocolBox,
            authBox,
            usernameBox,
            secretBox,
            secretPanel,
            privateKeyPanel,
            sftpTrustPanel,
            ftpOptionsPanel,
            insecureBox);

        // Port follows the protocol until the user types their own.
        var applyingDefaultPort = false;
        var portEdited = profile is not null
            && profile.Port > 0
            && profile.Port != DefaultPortFor(profile.Protocol);
        portBox.ValueChanged += (_, _) =>
        {
            if (!applyingDefaultPort)
            {
                portEdited = true;
            }
        };
        void ApplyDefaultPort()
        {
            if (portEdited)
            {
                return;
            }

            applyingDefaultPort = true;
            try
            {
                portBox.Value = DefaultPortFor(ComboTag(protocolBox, "sftp"));
            }
            finally
            {
                applyingDefaultPort = false;
            }
        }

        // Plain FTP needs explicit consent before the profile can be saved.
        void UpdatePlainFtpConsent()
        {
            var needsConsent = StringComparer.OrdinalIgnoreCase.Equals(ComboTag(protocolBox, "sftp"), "ftp")
                && insecureBox.IsChecked != true;
            insecureCaption.Visibility = needsConsent ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = !needsConsent;
        }

        protocolBox.SelectionChanged += (_, _) =>
        {
            ApplyDefaultPort();
            UpdatePlainFtpConsent();
        };
        insecureBox.Checked += (_, _) => UpdatePlainFtpConsent();
        insecureBox.Unchecked += (_, _) => UpdatePlainFtpConsent();
        ApplyDefaultPort();
        UpdatePlainFtpConsent();

        RemoteProfileDialogResult? testedResult = null;
        dialog.SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                var input = BuildRemoteProfileInput(
                    profile,
                    nameBox,
                    protocolBox,
                    hostBox,
                    portBox,
                    usernameBox,
                    rootBox,
                    authBox,
                    passiveBox,
                    insecureBox,
                    credentialBox,
                    privateKeyPathBox,
                    fingerprintBox);
                var secret = string.IsNullOrWhiteSpace(secretBox.Password) ? null : secretBox.Password;
                var result = await _viewModel.TestProfileAsync(input, secret);
                statusBlock.Text = result.Message;
                testedResult = new RemoteProfileDialogResult(input, secret);
            }
            catch (Exception exception)
            {
                statusBlock.Text = exception.Message;
            }
            finally
            {
                deferral.Complete();
            }
        };

        var result = await DialogHost.ShowAsync(dialog);
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        return new RemoteProfileDialogResult(
            BuildRemoteProfileInput(
                profile,
                nameBox,
                protocolBox,
                hostBox,
                portBox,
                usernameBox,
                rootBox,
                authBox,
                passiveBox,
                insecureBox,
                credentialBox,
                privateKeyPathBox,
                fingerprintBox),
            SecretToSave(secretBox, saveSecretBox))
        {
            LastTested = testedResult,
        };
    }

    internal const string PlainFtpConsentText = "Connect without encryption. Passwords and files are sent in plain text.";
    internal const string PlainFtpConsentRequiredText = "Confirm the unencrypted connection to save this profile";

    private static int DefaultPortFor(string? protocol) =>
        StringComparer.OrdinalIgnoreCase.Equals(protocol, "sftp") ? 22 : 21;

    private async Task<bool> ConfirmRemoteDeleteAsync(int count)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = count == 1 ? "Delete remote item" : "Delete remote items",
            Content = count == 1 ? "Delete the selected remote item?" : $"Delete {count} selected remote items?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await DialogHost.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private static StackPanel CreateProfileDialogSection(string title, params UIElement[] children)
    {
        var section = new StackPanel
        {
            Spacing = 8,
        };
        section.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        foreach (var child in children)
        {
            section.Children.Add(child);
        }

        return section;
    }

    private static void ApplyProfileDialogVisibility(
        ComboBox protocolBox,
        ComboBox authBox,
        TextBox usernameBox,
        PasswordBox secretBox,
        StackPanel secretPanel,
        StackPanel privateKeyPanel,
        StackPanel sftpTrustPanel,
        StackPanel ftpOptionsPanel,
        CheckBox insecureBox)
    {
        var protocol = ComboTag(protocolBox, "sftp");
        var usesFtpFamily = StringComparer.OrdinalIgnoreCase.Equals(protocol, "ftp")
            || StringComparer.OrdinalIgnoreCase.Equals(protocol, "ftps");
        foreach (var item in authBox.Items.OfType<ComboBoxItem>())
        {
            var tag = item.Tag?.ToString() ?? "";
            item.IsEnabled = !usesFtpFamily
                || StringComparer.OrdinalIgnoreCase.Equals(tag, "password")
                || StringComparer.OrdinalIgnoreCase.Equals(tag, "anonymous");
        }

        var auth = ComboTag(authBox, "password");
        if (usesFtpFamily
            && !StringComparer.OrdinalIgnoreCase.Equals(auth, "password")
            && !StringComparer.OrdinalIgnoreCase.Equals(auth, "anonymous"))
        {
            SelectCombo(authBox, "password");
            auth = "password";
        }

        var usesPassword = StringComparer.OrdinalIgnoreCase.Equals(auth, "password");
        var usesPrivateKey = StringComparer.OrdinalIgnoreCase.Equals(auth, "private-key");
        var usesAnonymous = StringComparer.OrdinalIgnoreCase.Equals(auth, "anonymous");
        usernameBox.Header = usesAnonymous ? "Username (optional)" : "Username";
        secretBox.Header = usesPrivateKey ? "Private key passphrase" : "Password";
        secretPanel.Visibility = usesPassword || usesPrivateKey ? Visibility.Visible : Visibility.Collapsed;
        privateKeyPanel.Visibility = usesPrivateKey ? Visibility.Visible : Visibility.Collapsed;
        sftpTrustPanel.Visibility = usesFtpFamily ? Visibility.Collapsed : Visibility.Visible;
        ftpOptionsPanel.Visibility = usesFtpFamily ? Visibility.Visible : Visibility.Collapsed;
        insecureBox.Visibility = StringComparer.OrdinalIgnoreCase.Equals(protocol, "ftp")
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!StringComparer.OrdinalIgnoreCase.Equals(protocol, "ftp"))
        {
            insecureBox.IsChecked = false;
        }
    }

    private static RemoteProfileInput BuildRemoteProfileInput(
        RemoteProfile? existing,
        TextBox nameBox,
        ComboBox protocolBox,
        TextBox hostBox,
        NumberBox portBox,
        TextBox usernameBox,
        TextBox rootBox,
        ComboBox authBox,
        CheckBox passiveBox,
        CheckBox insecureBox,
        TextBox credentialBox,
        TextBox privateKeyPathBox,
        TextBox fingerprintBox)
    {
        return new RemoteProfileInput
        {
            Id = existing?.Id,
            Name = nameBox.Text.Trim(),
            Protocol = ComboTag(protocolBox, "sftp"),
            Host = hostBox.Text.Trim(),
            Port = double.IsNaN(portBox.Value) ? 22 : (int)portBox.Value,
            Username = usernameBox.Text.Trim(),
            RootPath = string.IsNullOrWhiteSpace(rootBox.Text) ? "/" : rootBox.Text.Trim(),
            AuthKind = ComboTag(authBox, "password"),
            PassiveMode = passiveBox.IsChecked != false,
            InsecurePlainFtp = insecureBox.IsChecked == true,
            CredentialTarget = string.IsNullOrWhiteSpace(credentialBox.Text) ? null : credentialBox.Text.Trim(),
            PrivateKeyPath = string.IsNullOrWhiteSpace(privateKeyPathBox.Text) ? null : privateKeyPathBox.Text.Trim(),
            TrustedHostFingerprint = string.IsNullOrWhiteSpace(fingerprintBox.Text) ? null : fingerprintBox.Text.Trim(),
        };
    }

    private static void SelectCombo(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(item.Tag?.ToString(), tag))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static string ComboTag(ComboBox combo, string fallback) =>
        combo.SelectedItem is ComboBoxItem item && item.Tag is not null
            ? item.Tag.ToString() ?? fallback
            : fallback;

    private static string? SecretToSave(PasswordBox secretBox, CheckBox saveSecretBox)
    {
        if (saveSecretBox.IsChecked != true || string.IsNullOrWhiteSpace(secretBox.Password))
        {
            return null;
        }

        return secretBox.Password;
    }

    private sealed record RemoteProfileDialogResult(RemoteProfileInput Profile, string? Secret)
    {
        public RemoteProfileDialogResult? LastTested { get; init; }
    }
}
