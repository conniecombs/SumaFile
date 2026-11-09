using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.PluginSdk;
using SumaFile.Remote.Model;

namespace SumaFile.Remote.Views;

/// <summary>The profile the user saved, and the secret to store with it (null keeps the saved one).</summary>
internal sealed record RemoteProfileDialogResult(RemoteProfileInput Profile, string? Secret);

/// <summary>
/// The new and edit profile dialog, ported from the 1.0.4 manager. It is built in code and shown on the manager
/// window's own XamlRoot through the host's dialog gate. Plain FTP needs explicit consent before the profile saves.
/// </summary>
internal static class RemoteProfileDialog
{
    internal const string PlainFtpConsentText = "Connect without encryption. Passwords and files are sent in plain text.";
    internal const string PlainFtpConsentRequiredText = "Confirm the unencrypted connection to save this profile";

    /// <summary>Shows the dialog; null when the user cancelled.</summary>
    /// <param name="test">Runs the Test button against the backend.</param>
    /// <param name="pickPrivateKey">Opens the manager window's file picker for a private key.</param>
    public static async Task<RemoteProfileDialogResult?> ShowAsync(
        IPluginHost host,
        XamlRoot root,
        RemoteProfile? profile,
        Func<RemoteProfileInput, string?, Task<RemoteConnectionTestResult>> test,
        Func<Task<string?>> pickPrivateKey)
    {
        var mutedBrush = Xaml.Brush("SfTextMutedBrush");
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
            Foreground = mutedBrush,
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
            Style = Xaml.Resource<Style>("SfGhostButtonStyle"),
        };
        privateKeyPickerButton.Click += async (_, _) =>
        {
            try
            {
                var path = await pickPrivateKey();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    privateKeyPathBox.Text = path;
                }
            }
            catch (Exception exception)
            {
                host.Logger.Warn("The private key picker failed: " + exception.Message);
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
        // The credential target is an implementation detail: keep its value for saving but do not show an editable
        // box; it is surfaced as a tooltip on the Authentication section.
        var credentialTarget = profile?.CredentialTarget ?? "";
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
        var insecureCaption = new TextBlock
        {
            Foreground = Xaml.Brush("SystemFillColorCriticalBrush", "SfTextMutedBrush"),
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
            Foreground = mutedBrush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        AutomationProperties.SetAutomationId(nameBox, RemoteAutomationIds.ProfileName);
        AutomationProperties.SetAutomationId(protocolBox, RemoteAutomationIds.ProfileProtocol);
        AutomationProperties.SetAutomationId(hostBox, RemoteAutomationIds.ProfileHost);
        AutomationProperties.SetAutomationId(portBox, RemoteAutomationIds.ProfilePort);
        AutomationProperties.SetAutomationId(usernameBox, RemoteAutomationIds.ProfileUser);
        AutomationProperties.SetAutomationId(rootBox, RemoteAutomationIds.ProfileRootPath);
        AutomationProperties.SetAutomationId(authBox, RemoteAutomationIds.ProfileAuth);
        AutomationProperties.SetAutomationId(secretBox, RemoteAutomationIds.ProfileSecret);
        AutomationProperties.SetAutomationId(saveSecretBox, RemoteAutomationIds.ProfileSaveSecret);
        AutomationProperties.SetAutomationId(privateKeyPathBox, RemoteAutomationIds.ProfileKeyPath);
        AutomationProperties.SetAutomationId(passiveBox, RemoteAutomationIds.ProfilePassiveMode);
        AutomationProperties.SetAutomationId(insecureBox, RemoteAutomationIds.ProfileAllowPlainFtp);

        var authSection = CreateProfileDialogSection(
            "Authentication",
            authBox,
            secretPanel,
            privateKeyPanel,
            sftpTrustPanel);
        if (!string.IsNullOrWhiteSpace(credentialTarget))
        {
            ToolTipService.SetToolTip(authSection, $"Saved in Windows Credential Manager as {credentialTarget}");
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
            XamlRoot = root,
            Title = profile is null ? "New remote profile" : "Edit remote profile",
            Content = scrollViewer,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Test",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        void ApplyVisibility() => ApplyProfileDialogVisibility(
            protocolBox,
            authBox,
            usernameBox,
            secretBox,
            secretPanel,
            privateKeyPanel,
            sftpTrustPanel,
            ftpOptionsPanel,
            insecureBox);
        protocolBox.SelectionChanged += (_, _) => ApplyVisibility();
        authBox.SelectionChanged += (_, _) => ApplyVisibility();
        ApplyVisibility();

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

        RemoteProfileInput Build() => BuildRemoteProfileInput(
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
            credentialTarget,
            privateKeyPathBox,
            fingerprintBox);

        dialog.SecondaryButtonClick += async (_, args) =>
        {
            // Test keeps the dialog open and reports in its status line.
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                var secret = string.IsNullOrWhiteSpace(secretBox.Password) ? null : secretBox.Password;
                var result = await test(Build(), secret);
                statusBlock.Text = result.Message;
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

        var outcome = await host.Dialogs.ShowDialogAsync(dialog, CancellationToken.None);
        if (outcome != PluginDialogResult.Primary)
        {
            return null;
        }

        return new RemoteProfileDialogResult(Build(), SecretToSave(secretBox, saveSecretBox));
    }

    private static int DefaultPortFor(string? protocol) =>
        StringComparer.OrdinalIgnoreCase.Equals(protocol, "sftp") ? 22 : 21;

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
        string credentialTarget,
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
            CredentialTarget = string.IsNullOrWhiteSpace(credentialTarget) ? null : credentialTarget.Trim(),
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
}
