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
        await dialog.ShowAsync();
    }

    private async Task ShowQuickLookAsync()
    {
        if (ActiveSelectedRow is not { } row)
        {
            return;
        }

        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        var body = new StackPanel { Spacing = 8, Width = 560 };
        body.Children.Add(new TextBlock { Text = row.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = row.Path, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        body.Children.Add(new TextBlock { Text = $"{row.TypeText}  {row.SizeText}  {row.ModifiedText}" });
        var hasVisualPreview = false;
        Action? cleanup = null;

        if (fileOps is not null && row.IsDir)
        {
            var utilityCts = BeginUtilityOperation();
            try
            {
                var metrics = await fileOps.GetFolderMetricsAsync(row.Path, utilityCts.Token)
                    .ConfigureAwait(true);

                if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
                {
                    return;
                }

                var statsPanel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
                statsPanel.Children.Add(new TextBlock
                {
                    Text = "Folder Contents",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Opacity = 0.7,
                    FontSize = 12,
                });
                AddQuickLookMetadataRows(statsPanel, InspectionDetails.FolderMetricRows(metrics));
                body.Children.Add(statsPanel);
                hasVisualPreview = true;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                body.Children.Add(new TextBlock { Text = exception.Message });
            }
            finally
            {
                FinishUtilityOperation(utilityCts);
            }
        }
        else if (fileOps is not null && !row.IsDir)
        {
            var utilityCts = BeginUtilityOperation();
            try
            {
                var preview = await fileOps.ReadFilePreviewAsync(row.Path, 80_000, utilityCts.Token);
                if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
                {
                    return;
                }

                if (preview.FileType == "text" && preview.Content is not null)
                {
                    body.Children.Add(new TextBox
                    {
                        Text = preview.Content,
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                        FontSize = 12,
                        MaxHeight = 280,
                    });
                    hasVisualPreview = true;
                }
                else if (preview.FileType == "image" && await TryAddQuickLookImageAsync(body, row, preview, fileOps, utilityCts.Token))
                {
                    hasVisualPreview = true;
                }
                else if (PreviewPresenter.TryCreatePathBackedPreview(
                    row,
                    preview,
                    520,
                    out var pathBackedPreview,
                    out var pathBackedPreviewCleanup)
                    && pathBackedPreview is not null)
                {
                    body.Children.Add(pathBackedPreview);
                    cleanup = pathBackedPreviewCleanup;
                    hasVisualPreview = true;
                }
                else
                {
                    body.Children.Add(PreviewPresenter.CreateFileTypePreviewIcon(row, 96));
                    body.Children.Add(new TextBlock { Text = PreviewPresenter.IconPreviewMessage(preview), TextWrapping = TextWrapping.Wrap });
                    hasVisualPreview = true;
                }

                // Rich file metadata: show structured properties when available.
                try
                {
                    var metadata = await fileOps.GetFileMetadataAsync(row.Path, utilityCts.Token);
                    if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
                    {
                        return;
                    }

                    var detailRows = InspectionDetails.MetadataRows(
                        metadata,
                        includeSummary: false,
                        includeKind: false,
                        maxFields: 12);
                    if (detailRows.Count > 0)
                    {
                        var metaPanel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
                        metaPanel.Children.Add(new TextBlock
                        {
                            Text = InspectionDetails.MetadataHeading(metadata),
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Opacity = 0.7,
                            FontSize = 12,
                        });

                        AddQuickLookMetadataRows(metaPanel, detailRows);

                        if (metadata.Fields.Count > 12)
                        {
                            metaPanel.Children.Add(new TextBlock
                            {
                                Text = InspectionDetails.MoreFieldsText(metadata.Fields.Count - 12),
                                Opacity = 0.6,
                                FontSize = 12,
                            });
                        }

                        body.Children.Add(metaPanel);
                    }
                }
                catch
                {
                    // Best-effort: metadata extraction may fail for some file types.
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                body.Children.Add(new TextBlock { Text = exception.Message });
            }
            finally
            {
                FinishUtilityOperation(utilityCts);
            }
        }

        if (!hasVisualPreview)
        {
            body.Children.Add(PreviewPresenter.CreateFileTypePreviewIcon(row, 96));
        }

        if (workspace is not null && !ReferenceEquals(_workspace, workspace))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Quick Look",
            Content = body,
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        };
        if (cleanup is not null)
        {
            dialog.Closed += (_, _) => cleanup();
        }
        await dialog.ShowAsync();
    }

    private static void AddQuickLookMetadataRows(StackPanel panel, IEnumerable<InspectionDetailRow> rows)
    {
        foreach (var row in rows)
        {
            panel.Children.Add(QuickLookMetadataRow(row.Label, row.Value));
        }
    }

    private static Grid QuickLookMetadataRow(string label, string value)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            Opacity = 0.7,
            FontSize = 13,
        };
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        var valueBlock = new TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        };
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(valueBlock);

        return grid;
    }

    private static async Task<bool> TryAddQuickLookImageAsync(
        StackPanel body,
        FileRow row,
        FilePreview preview,
        FileOperationService fileOps,
        CancellationToken cancellationToken)
    {
        var imageData = preview.Content;
        if (string.IsNullOrWhiteSpace(imageData))
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
            var source = await PreviewImageSourceFactory.FromBase64Async(imageData, row.Path);
            body.Children.Add(new Image
            {
                Source = source,
                MaxHeight = 420,
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
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null || ActiveSelectedRow is not { } row)
        {
            return;
        }

        var rows = new StackPanel { Spacing = 8, Width = 460 };
        void AddRow(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            rows.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Opacity = 0.7,
            });
            rows.Children.Add(new TextBlock
            {
                Text = value,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        foreach (var detail in InspectionDetails.PropertiesRows(row, workspace.Active.Path))
        {
            AddRow(detail.Label, detail.Value);
        }

        var checksumText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12 };
        var checksumButton = new Button { Content = "Compute checksums", HorizontalAlignment = HorizontalAlignment.Left };

        var utilityCts = BeginUtilityOperation();
        try
        {
            var info = await fileOps.GetEntryInfoAsync(row.Path, utilityCts.Token);
            if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
            {
                return;
            }

            var attributes = new List<string>();
            if (info.IsHidden)
            {
                attributes.Add("Hidden");
            }

            if (info.IsSystem)
            {
                attributes.Add("System");
            }

            if (info.IsSymlink)
            {
                attributes.Add("Shortcut");
            }

            AddRow("Attributes", attributes.Count == 0 ? "Normal" : string.Join(", ", attributes));
            if (!string.IsNullOrEmpty(info.Permissions))
            {
                AddRow("Permissions", info.Permissions);
            }

            try
            {
                var metadata = await fileOps.GetFileMetadataAsync(row.Path, utilityCts.Token);
                foreach (var detail in InspectionDetails.MetadataRows(metadata, includeSummary: true))
                {
                    AddRow(detail.Label, detail.Value);
                }
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
            AddRow("Error", exception.Message);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }

        checksumButton.Click += async (_, _) =>
        {
            checksumButton.IsEnabled = false;
            checksumText.Text = "Computing…";
            var hashCts = BeginUtilityOperation();
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

        rows.Children.Add(checksumButton);
        rows.Children.Add(checksumText);

        if (!ReferenceEquals(_workspace, workspace))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Properties",
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                Content = rows,
            },
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
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
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedItem is not ClipboardHistoryRow row)
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
            await ShowQuickLookAsync();
            return;
        }

        if (folders.Length == 0)
        {
            SetStatusText("Select two or more folders to compare metrics.");
            return;
        }

        var paths = folders.Select(f => f.Path).ToArray();

        var utilityCts = BeginUtilityOperation();
        try
        {
            var lines = new List<string>();
            ulong totalSize = 0;
            ulong totalCount = 0;

            foreach (var path in paths)
            {
                SetStatusText($"Calculating metrics for {path}...");
                var metrics = await fileOps.GetFolderMetricsAsync(path, utilityCts.Token);
                if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
                {
                    return;
                }

                lines.Add($"{path}{Environment.NewLine}{EntryPresentation.FormatFileSize(metrics.Size)} · {metrics.ItemCount} item(s)");
                totalSize += metrics.Size;
                totalCount += metrics.ItemCount;
            }

            if (paths.Length > 1)
            {
                lines.Add($"Total: {EntryPresentation.FormatFileSize(totalSize)} · {totalCount} item(s) across {paths.Length} folders");
            }
            else
            {
                lines.Add($"Total: {EntryPresentation.FormatFileSize(totalSize)} · {totalCount} item(s)");
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
            await dialog.ShowAsync();
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

        var dialog = new ContentDialog
        {
            Title = "Operation history",
            Content = list,
            PrimaryButtonText = "Retry",
            SecondaryButtonText = workspace.Undo.CanUndo ? "Undo last" : "",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dialog.ShowAsync();
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

    private async Task RunGitAsync(bool pull)
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null)
        {
            return;
        }

        var path = workspace.Active.Path;
        var utilityCts = BeginUtilityOperation();
        try
        {
            if (pull)
            {
                SetStatusText($"Pulling Git changes in {path}...");
                await fileOps.GitPullAsync(path, utilityCts.Token);
                if (ReferenceEquals(_workspace, workspace) && !utilityCts.IsCancellationRequested)
                {
                    ShowMessage("Git", "Pull completed.", InfoBarSeverity.Success);
                }
            }
            else
            {
                SetStatusText($"Pushing Git changes from {path}...");
                await fileOps.GitPushAsync(path, utilityCts.Token);
                if (ReferenceEquals(_workspace, workspace) && !utilityCts.IsCancellationRequested)
                {
                    ShowMessage("Git", "Push completed.", InfoBarSeverity.Success);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage(pull ? "Git pull" : "Git push", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
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

        var utilityCts = BeginUtilityOperation();
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

