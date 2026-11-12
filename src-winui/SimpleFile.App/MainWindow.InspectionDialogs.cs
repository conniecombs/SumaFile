using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace SimpleFile.App;

public sealed partial class MainWindow
{

    private async Task ShowKeyboardHelpAsync()
    {
        var lines = KeyboardShortcutMap
            .EffectiveShortcuts(_workspace?.Settings.ShortcutOverrides)
            .Select(item => $"{item.Keys,-28}  {item.Label}");
        var box = new TextBox
        {
            Text = string.Join(Environment.NewLine, lines),
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            MinWidth = 420,
            MaxHeight = 360,
        };
        var dialog = new ContentDialog
        {
            Title = "Keyboard shortcuts",
            Content = box,
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        };
        await DialogHost.ShowAsync(dialog);
    }

    // Same text budget as the preview pane, so Quick Look never shows less than the pane does.
    private const int QuickLookTextBudget = 2_000_000;

    /// <summary>
    /// Quick Look is a lightbox: wide, the file name as the only header, Left / Right step to the
    /// adjacent item in the active pane, Space or Escape close. Folders open Properties instead.
    /// </summary>
    private async Task ShowQuickLookAsync()
    {
        if (ActiveSelectedRow is not { } row)
        {
            return;
        }

        if (row.IsDir)
        {
            await ShowPropertiesAsync();
            return;
        }

        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        var pane = workspace?.ActivePane ?? PaneId.Primary;
        var windowWidth = (Content as FrameworkElement)?.ActualWidth ?? 0;
        var windowHeight = (Content as FrameworkElement)?.ActualHeight ?? 0;
        var width = Math.Max(480, windowWidth > 0 ? windowWidth * 0.8 : 720);
        var previewHeight = Math.Max(240, windowHeight > 0 ? windowHeight * 0.62 : 480);
        var host = new Grid { Width = width, MinHeight = 200 };
        Action? cleanup = null;

        var utilityCts = BeginUtilityOperation("ShowQuickLookAsync");
        try
        {
            var (body, bodyCleanup) = await DialogHost.RunWithProgressAsync(
                Content.XamlRoot,
                "Quick Look",
                $"Loading {row.Name}…",
                (_, token) => LoadQuickLookBodyAsync(row, fileOps, width, previewHeight, token),
                utilityCts.Token);
            if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
            {
                bodyCleanup?.Invoke();
                return;
            }

            host.Children.Add(body);
            cleanup = bodyCleanup;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }

        var dialog = new ContentDialog
        {
            Title = row.Name,
            Content = host,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
            KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden,
        };
        dialog.Resources["ContentDialogMaxWidth"] = width + 64;
        dialog.Resources["ContentDialogMaxHeight"] = previewHeight + 200;

        var current = row;
        CancellationTokenSource? navigationCts = null;
        async Task StepAsync(int delta)
        {
            var rows = pane == PaneId.Secondary ? SecondaryFiles : PrimaryFiles;
            var index = rows.IndexOf(current);
            if (index < 0)
            {
                index = rows.ToList().FindIndex(candidate => PathRules.PathsEqual(candidate.Path, current.Path));
            }

            var nextIndex = index + delta;
            if (index < 0 || nextIndex < 0 || nextIndex >= rows.Count)
            {
                return;
            }

            current = rows[nextIndex];
            SelectRow(pane, current.Path);
            dialog.Title = current.Name;
            navigationCts?.Cancel();
            var cts = new CancellationTokenSource();
            navigationCts = cts;
            cleanup?.Invoke();
            cleanup = null;
            host.Children.Clear();
            host.Children.Add(new ProgressRing
            {
                IsActive = true,
                Width = 32,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            try
            {
                var (body, bodyCleanup) = await LoadQuickLookBodyAsync(current, fileOps, width, previewHeight, cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(navigationCts, cts))
                {
                    bodyCleanup?.Invoke();
                    return;
                }

                host.Children.Clear();
                host.Children.Add(body);
                cleanup = bodyCleanup;
            }
            catch (OperationCanceledException)
            {
            }
        }

        void AddAccelerator(VirtualKey key, Action invoke)
        {
            var accelerator = new KeyboardAccelerator { Key = key };
            accelerator.Invoked += (_, args) =>
            {
                args.Handled = true;
                invoke();
            };
            dialog.KeyboardAccelerators.Add(accelerator);
        }

        AddAccelerator(VirtualKey.Left, () => _ = StepAsync(-1));
        AddAccelerator(VirtualKey.Right, () => _ = StepAsync(1));
        AddAccelerator(VirtualKey.Space, dialog.Hide);
        dialog.Closed += (_, _) =>
        {
            navigationCts?.Cancel();
            cleanup?.Invoke();
            cleanup = null;
        };
        await DialogHost.ShowAsync(dialog);
    }

    /// <summary>The Quick Look body for one item: the preview only, no metadata card.</summary>
    private async Task<(UIElement Body, Action? Cleanup)> LoadQuickLookBodyAsync(
        FileRow row,
        FileOperationService? fileOps,
        double width,
        double previewHeight,
        CancellationToken cancellationToken)
    {
        var body = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (row.IsDir || fileOps is null)
        {
            body.Children.Add(PreviewPresenter.CreateFileTypePreviewIcon(row, 96));
            return (body, null);
        }

        Action? cleanup = null;
        try
        {
            var preview = await fileOps.ReadFilePreviewAsync(row.Path, QuickLookTextBudget, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (preview.FileType == "text" && preview.Content is not null)
            {
                body.Children.Add(new TextBox
                {
                    Text = preview.Content,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.NoWrap,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 12,
                    Height = previewHeight,
                });
            }
            else if (preview.FileType == "image"
                && await TryAddQuickLookImageAsync(body, row, preview, fileOps, previewHeight, cancellationToken))
            {
                // The image is in the body.
            }
            else if (PreviewPresenter.TryCreatePathBackedPreview(
                row,
                preview,
                previewHeight,
                out var pathBackedPreview,
                out var pathBackedPreviewCleanup)
                && pathBackedPreview is not null)
            {
                body.Children.Add(pathBackedPreview);
                cleanup = pathBackedPreviewCleanup;
            }
            else
            {
                body.Children.Add(PreviewPresenter.CreateFileTypePreviewIcon(row, 96));
                body.Children.Add(new TextBlock { Text = PreviewPresenter.IconPreviewMessage(preview), TextWrapping = TextWrapping.Wrap });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            body.Children.Add(PreviewPresenter.CreateFileTypePreviewIcon(row, 96));
            body.Children.Add(new TextBlock { Text = exception.Message, TextWrapping = TextWrapping.Wrap });
        }

        return (body, cleanup);
    }

    private static async Task<bool> TryAddQuickLookImageAsync(
        StackPanel body,
        FileRow row,
        FilePreview preview,
        FileOperationService fileOps,
        double maxHeight,
        CancellationToken cancellationToken)
    {
        byte[]? imageData = string.IsNullOrWhiteSpace(preview.Content)
            ? null
            : Convert.FromBase64String(preview.Content);
        if (imageData is null || imageData.Length == 0)
        {
            try
            {
                imageData = await fileOps.GenerateThumbnailAsync(row.Path, 512, cancellationToken);
            }
            catch
            {
                return false;
            }
        }

        try
        {
            var source = await PreviewImageSourceFactory.FromBytesAsync(imageData, row.Path);
            body.Children.Add(new Image
            {
                Source = source,
                MaxHeight = maxHeight,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Stretch = Stretch.Uniform,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ShowPropertiesAsync()
    {
        var workspace = _workspace;
        if (workspace is null || workspace.FileOps is null || ActiveSelectedRow is not { } row)
        {
            return;
        }

        await ShowPropertiesForRowAsync(workspace, row);
    }

    // Background-menu Properties: the active pane's folder itself, not a selected row.
    private async Task ShowCurrentFolderPropertiesAsync()
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        var path = workspace?.Active.Path;
        if (workspace is null || fileOps is null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        FileRow row;
        var lookupCts = BeginUtilityOperation("FolderProperties");
        try
        {
            var entry = await fileOps.GetEntryInfoAsync(workspace.Active.Path, lookupCts.Token);
            if (!ReferenceEquals(_workspace, workspace) || lookupCts.IsCancellationRequested)
            {
                return;
            }

            row = FileRow.From(entry, pane: workspace.ActivePane);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowMessage("Properties", exception.Message, InfoBarSeverity.Warning);
            return;
        }
        finally
        {
            FinishUtilityOperation(lookupCts);
        }

        await ShowPropertiesForRowAsync(workspace, row);
    }

    private async Task ShowPropertiesForRowAsync(ExplorerWorkspace workspace, FileRow row)
    {
        var fileOps = workspace.FileOps;
        if (fileOps is null)
        {
            return;
        }

        // A heading for the item, then one titled card per group, each a two-column label/value table.
        var rows = new StackPanel { Spacing = 14, Width = 480 };
        var heading = new StackPanel { Spacing = 2 };
        heading.Children.Add(new TextBlock
        {
            Text = row.Name,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        heading.Children.Add(new TextBlock { Text = row.TypeText, FontSize = 12, Foreground = Brush("SfTextMutedBrush") });
        rows.Children.Add(heading);

        FileEntry? info = null;
        FileMetadata? metadata = null;
        string? lookupError = null;
        var utilityCts = BeginUtilityOperation("Properties");
        try
        {
            info = await fileOps.GetEntryInfoAsync(row.Path, utilityCts.Token);
            if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
            {
                return;
            }

            try
            {
                metadata = await fileOps.GetFileMetadataAsync(row.Path, utilityCts.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Metadata is optional; core properties still show.
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lookupError = exception.Message;
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }

        foreach (var section in InspectionDetails.PropertiesSections(row, workspace.Active.Path, info, metadata))
        {
            PropertiesTable(PropertiesSection(rows, section.Title), section.Rows);
        }

        if (lookupError is not null)
        {
            PropertiesTable(PropertiesSection(rows, "Error"), [new InspectionDetailRow("Details", lookupError)]);
        }

        // The service refuses checksums for directories, so only files get the section.
        if (!row.IsDir)
        {
            var checksumText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 12,
                IsTextSelectionEnabled = true,
                Visibility = Visibility.Collapsed,
            };
            var checksumButton = new Button { Content = "Compute checksums", HorizontalAlignment = HorizontalAlignment.Left };
            checksumButton.Click += async (_, _) =>
            {
                checksumButton.IsEnabled = false;
                checksumText.Visibility = Visibility.Visible;
                checksumText.Text = "Computing…";
                var hashCts = BeginUtilityOperation("PropertiesChecksum");
                try
                {
                    var checksums = await fileOps.ComputeChecksumAsync(row.Path, hashCts.Token);
                    checksumText.Text = InspectionDetails.ChecksumsText(checksums);
                }
                catch (Exception exception)
                {
                    checksumText.Text = exception.Message;
                }
                finally
                {
                    FinishUtilityOperation(hashCts);
                    checksumButton.IsEnabled = true;
                }
            };
            var checksumSection = PropertiesSection(rows, "Checksums");
            checksumSection.Children.Add(checksumButton);
            checksumSection.Children.Add(checksumText);
        }

        if (!ReferenceEquals(_workspace, workspace))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Properties",
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                Padding = new Thickness(0, 0, 12, 0),
                Content = rows,
            },
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        };

        if (row.IsDir)
        {
            // Folder metrics can take a while on large trees, so they load after the dialog
            // is visible and stop when it closes.
            var metricsHost = PropertiesSection(rows, "Contents");
            var metricsStatus = new TextBlock { Text = "Calculating…", TextWrapping = TextWrapping.Wrap, Foreground = Brush("SfTextMutedBrush") };
            metricsHost.Children.Add(metricsStatus);

            var metricsCts = BeginUtilityOperation("PropertiesFolderMetrics");
            dialog.Closed += (_, _) =>
            {
                try
                {
                    metricsCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The lookup already finished and the registry disposed its token source.
                }
            };
            _ = LoadFolderMetricsIntoAsync(fileOps, row.Path, metricsHost, metricsStatus, metricsCts);
        }

        await DialogHost.ShowAsync(dialog);
    }

    /// <summary>Adds a titled card to the Properties dialog and returns the panel its content goes in.</summary>
    private StackPanel PropertiesSection(StackPanel host, string title)
    {
        var body = new StackPanel { Spacing = 8 };
        var section = new StackPanel { Spacing = 6 };
        section.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("SfTextMutedBrush"),
        });
        section.Children.Add(new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("SfBorderBrush"),
            Background = Brush("SfBgSecondaryBrush"),
            Child = body,
        });
        host.Children.Add(section);
        return body;
    }

    /// <summary>Lays label/value rows out as a two-column table: muted labels left, selectable values right.</summary>
    private void PropertiesTable(StackPanel body, IReadOnlyList<InspectionDetailRow> details)
    {
        var table = new Grid { ColumnSpacing = 12, RowSpacing = 6 };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var detail in details)
        {
            var index = table.RowDefinitions.Count;
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = detail.Label, FontSize = 12, Foreground = Brush("SfTextMutedBrush"), TextWrapping = TextWrapping.Wrap };
            var value = new TextBlock { Text = detail.Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(label, index);
            Grid.SetRow(value, index);
            Grid.SetColumn(value, 1);
            table.Children.Add(label);
            table.Children.Add(value);
        }

        body.Children.Add(table);
    }

    private async Task LoadFolderMetricsIntoAsync(
        FileOperationService fileOps,
        string path,
        StackPanel host,
        TextBlock status,
        CancellationTokenSource cts)
    {
        try
        {
            var metrics = await fileOps.GetFolderMetricsAsync(path, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            host.Children.Remove(status);
            PropertiesTable(host, InspectionDetails.FolderMetricRows(metrics));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            status.Text = exception.Message;
        }
        finally
        {
            FinishUtilityOperation(cts);
        }
    }

    private async Task ShowClipboardHistoryAsync()
    {
        if (_workspace is null)
        {
            return;
        }

        var entries = _workspace.ClipboardHistory.Items;
        if (entries.Count == 0)
        {
            SetStatusText("Clipboard history is empty");
            return;
        }

        var list = new ListView
        {
            MinWidth = 420,
            MaxHeight = 320,
            SelectionMode = ListViewSelectionMode.Single,
        };
        foreach (var entry in entries)
        {
            list.Items.Add(new ClipboardHistoryRow(entry));
        }

        list.SelectedIndex = 0;
        var dialog = new ContentDialog
        {
            Title = "Clipboard history",
            Content = list,
            PrimaryButtonText = "Paste this",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await DialogHost.ShowAsync(dialog) != ContentDialogResult.Primary || list.SelectedItem is not ClipboardHistoryRow row)
        {
            return;
        }

        if (row.Entry.Operation == ClipboardOperation.Cut)
        {
            _workspace.Clipboard.SetCut(row.Entry.Paths);
        }
        else
        {
            _workspace.Clipboard.SetCopy(row.Entry.Paths);
        }

        await PasteFromClipboard();
    }

    private async Task ShowFolderMetricsAsync()
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null)
        {
            return;
        }

        var folders = ActiveSelectedRows.Where(row => row.IsDir).ToArray();
        if (folders.Length == 1)
        {
            // One folder: Properties measures it (Quick Look no longer shows folder metrics).
            await ShowPropertiesForRowAsync(workspace, folders[0]);
            return;
        }

        if (folders.Length == 0)
        {
            SetStatusText("Select two or more folders to compare metrics.");
            return;
        }

        var paths = folders.Select(f => f.Path).ToArray();

        var utilityCts = BeginUtilityOperation("ShowFolderMetricsAsync");
        try
        {
            var lines = await DialogHost.RunWithProgressAsync(
                Content.XamlRoot,
                "Folder metrics",
                $"Measuring {paths.Length} folders…",
                async (progress, token) =>
                {
                    var measured = new List<string>();
                    ulong totalSize = 0;
                    ulong totalCount = 0;
                    for (var index = 0; index < paths.Length; index++)
                    {
                        var path = paths[index];
                        progress.Report($"Measuring {index + 1} of {paths.Length}: {path}");
                        var metrics = await fileOps.GetFolderMetricsAsync(path, token);
                        measured.Add($"{path}{Environment.NewLine}{EntryPresentation.FormatFileSize(metrics.Size)} · {metrics.ItemCount} item(s)");
                        totalSize += metrics.Size;
                        totalCount += metrics.ItemCount;
                    }

                    measured.Add(paths.Length > 1
                        ? $"Total: {EntryPresentation.FormatFileSize(totalSize)} · {totalCount} item(s) across {paths.Length} folders"
                        : $"Total: {EntryPresentation.FormatFileSize(totalSize)} · {totalCount} item(s)");
                    return measured;
                },
                utilityCts.Token);
            if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
            {
                return;
            }

            SetStatusText("");
            var dialog = new ContentDialog
            {
                Title = "Folder metrics comparison",
                Content = new ScrollViewer
                {
                    MaxHeight = 400,
                    Content = new TextBlock
                    {
                        Text = string.Join(Environment.NewLine + Environment.NewLine, lines),
                        TextWrapping = TextWrapping.Wrap,
                        Width = 420,
                    },
                },
                CloseButtonText = "Close",
                XamlRoot = Content.XamlRoot,
            };
            await DialogHost.ShowAsync(dialog);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("Folder metrics", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    private async Task ShowOperationHistoryAsync()
    {
        var workspace = _workspace;
        var records = workspace?.OperationLog ?? [];
        if (workspace is null || records.Count == 0)
        {
            SetStatusText("No operations in this session.");
            return;
        }

        var list = new ListView
        {
            MinWidth = 420,
            MaxHeight = 320,
            SelectionMode = ListViewSelectionMode.Single,
        };
        foreach (var record in records)
        {
            list.Items.Add(new OperationHistoryRow(record));
        }

        list.SelectedIndex = 0;
        var statusText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var sourceText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
        var destinationText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
        void UpdateDetails()
        {
            if (list.SelectedItem is not OperationHistoryRow row)
            {
                statusText.Text = "";
                sourceText.Text = "";
                destinationText.Text = "";
                return;
            }

            statusText.Text = row.Display.StatusLabel;
            sourceText.Text = row.Display.SourceSummary;
            destinationText.Text = row.Display.DestinationSummary;
        }

        UpdateDetails();
        var content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                list,
                statusText,
                sourceText,
                destinationText,
            },
        };

        var dialog = new ContentDialog
        {
            Title = "Operation history",
            Content = content,
            PrimaryButtonText = OperationHistoryFormatter.RetrySelectedLabel,
            SecondaryButtonText = workspace.Undo.CanUndo
                ? (workspace.Undo.NextUndoDescription is { Length: > 0 } nextUndo ? $"Undo: {nextUndo}" : "Undo last")
                : "",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        // Retry only makes sense for transfers that did not complete.
        static bool CanRetry(object? item) =>
            item is OperationHistoryRow candidate
            && candidate.Record.Status is "failed" or "cancelled";
        dialog.IsPrimaryButtonEnabled = CanRetry(list.SelectedItem);
        list.SelectionChanged += (_, _) =>
        {
            UpdateDetails();
            dialog.IsPrimaryButtonEnabled = CanRetry(list.SelectedItem);
        };
        var result = await DialogHost.ShowAsync(dialog);
        if (!ReferenceEquals(_workspace, workspace))
        {
            return;
        }

        if (result == ContentDialogResult.Primary && list.SelectedItem is OperationHistoryRow row)
        {
            await TransferWithConflictAsync(
                row.Record.Sources,
                row.Record.Destination,
                row.Record.Move);
        }
        else if (result == ContentDialogResult.Secondary && workspace.Undo.CanUndo)
        {
            await UndoLastAsync();
        }
    }

    private async Task OpenPowershellAdminAsync()
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null)
        {
            return;
        }

        var utilityCts = BeginUtilityOperation("OpenPowershellAdminAsync");
        try
        {
            await fileOps.OpenPowershellAdminAsync(workspace.Active.Path, utilityCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("PowerShell", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

}

