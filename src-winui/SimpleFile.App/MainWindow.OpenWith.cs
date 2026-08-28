using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
using Windows.Storage.Pickers;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private OpenWithPreferences _openWithPreferences = new();
    private readonly Dictionary<string, IReadOnlyList<OpenWithApplication>> _openWithDiscoveryCache = new(StringComparer.OrdinalIgnoreCase);

    private async Task LoadOpenWithPreferencesAsync(FileOperationService fileOps, CancellationToken cancellationToken)
    {
        try
        {
            var json = await fileOps.GetSettingAsync(OpenWithPreferences.SettingsKey, cancellationToken);
            _openWithPreferences = OpenWithPreferences.FromJson(json);
        }
        catch
        {
            _openWithPreferences = new OpenWithPreferences();
        }
    }

    private async Task SaveOpenWithPreferencesAsync(FileOperationService fileOps, CancellationToken cancellationToken)
    {
        await fileOps.SetSettingAsync(OpenWithPreferences.SettingsKey, _openWithPreferences.ToJson(), cancellationToken);
    }

    private IReadOnlyList<OpenWithApplication> OpenWithApplicationsForPath(string path)
    {
        if (OpenWithPolicy.IsDeniedTargetPath(path))
        {
            return [];
        }

        var extension = OpenWithPreferences.NormalizeExtension(Path.GetExtension(path));
        if (!_openWithDiscoveryCache.TryGetValue(extension, out var discovered))
        {
            discovered = OpenWithApplicationDiscovery.ApplicationsForPath(path);
            _openWithDiscoveryCache[extension] = discovered;
        }

        return _openWithPreferences.ComposeMenuApplications(extension, discovered);
    }

    private async Task OpenSelectedWithApplicationAsync(string? applicationPath)
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null
            || fileOps is null
            || ActiveSelectedRow is not { IsDir: false } row
            || string.IsNullOrWhiteSpace(applicationPath))
        {
            return;
        }

        var application = OpenWithApplicationsForPath(row.Path)
            .FirstOrDefault(app => string.Equals(app.ApplicationPath, applicationPath.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? OpenWithApplication.FromPath(applicationPath);

        await LaunchOpenWithApplicationAsync(workspace, fileOps, row, application, favoriteChoice: null, statusText: null);
    }

    private async Task ShowOpenWithChooserAsync()
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null || ActiveSelectedRow is not { IsDir: false } row)
        {
            return;
        }

        if (OpenWithPolicy.IsDeniedTargetPath(row.Path))
        {
            ShowMessage("Open With", "Open With does not allow executable or script payload files.", InfoBarSeverity.Warning);
            return;
        }

        var apps = OpenWithApplicationsForPath(row.Path).ToList();
        var appList = new ListView
        {
            MaxHeight = 220,
            MinHeight = apps.Count > 0 ? 128 : 0,
            SelectionMode = ListViewSelectionMode.Single,
        };

        var input = new TextBox
        {
            Header = "Application",
            PlaceholderText = "Application name or full path",
            Text = apps.FirstOrDefault()?.ApplicationPath ?? "",
        };

        foreach (var app in apps)
        {
            appList.Items.Add(CreateOpenWithAppItem(app));
        }

        if (appList.Items.Count > 0)
        {
            appList.SelectedIndex = 0;
        }

        var extensionLabel = ExtensionLabelFor(row);
        var pinCheckBox = new CheckBox
        {
            Content = $"Pin for {extensionLabel} in SumaFile",
            IsChecked = apps.FirstOrDefault()?.IsFavorite == true,
        };

        var status = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.Firebrick),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        appList.SelectionChanged += (_, _) =>
        {
            if (appList.SelectedItem is ListViewItem { Tag: OpenWithApplication selected })
            {
                input.Text = selected.ApplicationPath;
                pinCheckBox.IsChecked = selected.IsFavorite;
                status.Visibility = Visibility.Collapsed;
            }
        };

        var browseButton = new Button { Content = "Browse..." };
        browseButton.Click += async (_, _) =>
        {
            var pickedPath = await PickOpenWithApplicationAsync();
            if (!string.IsNullOrWhiteSpace(pickedPath))
            {
                appList.SelectedItem = null;
                input.Text = pickedPath;
                pinCheckBox.IsChecked = false;
                status.Visibility = Visibility.Collapsed;
            }
        };

        var inputRow = new Grid { ColumnSpacing = 8 };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(input, 0);
        Grid.SetColumn(browseButton, 1);
        inputRow.Children.Add(input);
        inputRow.Children.Add(browseButton);

        var body = new StackPanel { Spacing = 12, Width = 440 };
        body.Children.Add(new TextBlock
        {
            Text = row.Name,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        if (apps.Count > 0)
        {
            body.Children.Add(appList);
        }
        else
        {
            body.Children.Add(new TextBlock
            {
                Text = "No matching apps were found yet.",
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        body.Children.Add(inputRow);
        body.Children.Add(pinCheckBox);
        body.Children.Add(status);

        var dialog = new ContentDialog
        {
            Title = "Open with",
            Content = body,
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                var selectedApplication = SelectedOpenWithApplication(appList, input.Text);
                if (selectedApplication is null)
                {
                    status.Text = "Choose an app or enter an application path.";
                    status.Visibility = Visibility.Visible;
                    return;
                }

                var opened = await LaunchOpenWithApplicationAsync(
                    workspace,
                    fileOps,
                    row,
                    selectedApplication,
                    pinCheckBox.IsChecked,
                    status);
                if (opened)
                {
                    dialog.Hide();
                }
            }
            finally
            {
                dialog.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    private OpenWithApplication? SelectedOpenWithApplication(ListView appList, string applicationText)
    {
        if (appList.SelectedItem is ListViewItem { Tag: OpenWithApplication selected }
            && string.Equals(selected.ApplicationPath, applicationText.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return selected;
        }

        return string.IsNullOrWhiteSpace(applicationText)
            ? null
            : OpenWithApplication.FromPath(applicationText);
    }

    private static ListViewItem CreateOpenWithAppItem(OpenWithApplication application)
    {
        var grid = new Grid
        {
            ColumnSpacing = 10,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 16,
            Glyph = ContextMenuIconCatalog.OpenWith,
            Width = 24,
            Height = 24,
        };

        var labels = new StackPanel { Spacing = 2 };
        labels.Children.Add(new TextBlock
        {
            Text = application.MenuLabel,
            TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
        });
        labels.Children.Add(new TextBlock
        {
            Text = OpenWithSourceLabel(application),
            Opacity = 0.66,
            FontSize = 12,
            TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
        });

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(labels, 1);
        grid.Children.Add(icon);
        grid.Children.Add(labels);

        return new ListViewItem
        {
            Content = grid,
            Tag = application,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    private static string OpenWithSourceLabel(OpenWithApplication application)
    {
        if (application.IsFavorite)
        {
            return "Pinned";
        }

        if (application.IsRecent)
        {
            return "Recent";
        }

        return application.Source switch
        {
            "registered" => "Registered app",
            "suggested" => "Suggested app",
            _ => "Custom app",
        };
    }

    private async Task<bool> LaunchOpenWithApplicationAsync(
        ExplorerWorkspace workspace,
        FileOperationService fileOps,
        FileRow row,
        OpenWithApplication application,
        bool? favoriteChoice,
        TextBlock? statusText)
    {
        if (!ReferenceEquals(_workspace, workspace))
        {
            return false;
        }

        var utilityCts = BeginUtilityOperation();
        try
        {
            await SaveViewIconSizeNowAsync();
            await fileOps.OpenFileWithAsync(row.Path, application.ApplicationPath.Trim(), utilityCts.Token);
            _openWithPreferences.RecordRecent(row.Extension, application);
            if (favoriteChoice == true)
            {
                _openWithPreferences.PinForExtension(row.Extension, application);
            }
            else if (favoriteChoice == false)
            {
                _openWithPreferences.UnpinForExtension(row.Extension, application);
            }

            await SaveOpenWithPreferencesAsync(fileOps, utilityCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            if (statusText is null)
            {
                ShowMessage("Open With", exception.Message, InfoBarSeverity.Error);
            }
            else
            {
                statusText.Text = exception.Message;
                statusText.Visibility = Visibility.Visible;
            }

            return false;
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    private async Task<string?> PickOpenWithApplicationAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add(".com");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private static string ExtensionLabelFor(FileRow row)
    {
        var extension = OpenWithPreferences.NormalizeExtension(row.Extension);
        return extension == "*" ? "files without an extension" : extension;
    }
}
