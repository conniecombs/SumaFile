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
    /// Quick Look is a lightbox: wide, the file name as the header with the item's place among the
    /// pane's rows beside it, Left / Right step to the adjacent item in the active pane, Space or
    /// Escape close. An image zooms with + and - (0 fits it again) or Ctrl+wheel; a text file that
    /// can be rendered (Markdown, HTML...) switches between its source and the rendered view.
    /// Folders open Properties instead.
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
                (_, token) => LoadQuickLookBodyAsync(row, fileOps, workspace, width, previewHeight, token),
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

        string PositionOf(FileRow item)
        {
            var rows = pane == PaneId.Secondary ? SecondaryFiles : PrimaryFiles;
            var index = rows.IndexOf(item);
            if (index < 0)
            {
                index = rows.ToList().FindIndex(candidate => PathRules.PathsEqual(candidate.Path, item.Path));
            }

            return QuickLookText.Position(index, rows.Count);
        }

        var dialog = new ContentDialog
        {
            Title = QuickLookTitle(row.Name, PositionOf(row)),
            Content = host,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
            KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dialog, row.Name);
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
            dialog.Title = QuickLookTitle(current.Name, QuickLookText.Position(nextIndex, rows.Count));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dialog, current.Name);
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
                var (body, bodyCleanup) = await LoadQuickLookBodyAsync(current, fileOps, workspace, width, previewHeight, cts.Token);
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

        // The image being shown, when the item is one: its body carries the view that zooms.
        bool Zoom(int delta)
        {
            if ((host.Children.FirstOrDefault() as FrameworkElement)?.Tag is not ScrollViewer image)
            {
                return false;
            }

            image.ChangeView(null, null, (float)QuickLookZoom.Step(image.ZoomFactor, delta));
            return true;
        }

        // The lightbox's own keys are taken before whatever has the focus inside it: the text of a file would
        // otherwise use Left and Right for its caret, and stepping would work for some files and not for others.
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu))
            {
                return;
            }

            e.Handled = e.Key switch
            {
                VirtualKey.Left => Step(-1),
                VirtualKey.Right => Step(1),
                // Plus and minus on the main keys (187, 189) and on the number pad; 0 fits the image again.
                VirtualKey.Add or (VirtualKey)187 => Zoom(1),
                VirtualKey.Subtract or (VirtualKey)189 => Zoom(-1),
                VirtualKey.Number0 or VirtualKey.NumberPad0 => Zoom(0),
                _ => false,
            };
        };

        bool Step(int delta)
        {
            _ = StepAsync(delta);
            return true;
        }

        AddAccelerator(VirtualKey.Space, dialog.Hide);
        dialog.Closed += (_, _) =>
        {
            navigationCts?.Cancel();
            cleanup?.Invoke();
            cleanup = null;
        };
        await DialogHost.ShowAsync(dialog);
    }

    /// <summary>The lightbox's header: the name, and beside it, smaller, the item's place among the pane's rows.</summary>
    private static Grid QuickLookTitle(string name, string position)
    {
        var title = new Grid { ColumnSpacing = 12 };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
        var place = new TextBlock
        {
            Text = position,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SfTextMutedBrush"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(place, "QuickLook.Position");
        Grid.SetColumn(place, 1);
        title.Children.Add(place);
        return title;
    }

    /// <summary>The Quick Look body for one item: the preview only, no metadata card.</summary>
    private async Task<(UIElement Body, Action? Cleanup)> LoadQuickLookBodyAsync(
        FileRow row,
        FileOperationService? fileOps,
        ExplorerWorkspace? workspace,
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
                var source = new TextBox
                {
                    Text = preview.Content,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.NoWrap,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 12,
                    Height = previewHeight,
                };
                if (PreviewCapabilities.CanRenderAsHtml(row.Path, preview))
                {
                    cleanup = AddQuickLookRenderSwitch(
                        body,
                        source,
                        row.Path,
                        preview,
                        previewHeight,
                        rendered: workspace?.Settings.PreviewRenderHtml == true);
                }

                body.Children.Add(source);
            }
            else if (preview.FileType == "image"
                && await TryAddQuickLookImageAsync(body, row, preview, fileOps, width, previewHeight, cancellationToken))
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

    /// <summary>
    /// Puts a Source | Rendered switch above a text file that can be rendered, as the preview pane has. It opens in
    /// the mode the pane is set to; the switch here changes this Quick Look only. The rendered view is made when it
    /// is first asked for, and the returned action closes it.
    /// </summary>
    private static Action AddQuickLookRenderSwitch(
        StackPanel body,
        TextBox source,
        string path,
        FilePreview preview,
        double previewHeight,
        bool rendered)
    {
        var selector = new SelectorBar { HorizontalAlignment = HorizontalAlignment.Left };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(selector, "QuickLook.RenderMode");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(selector, "Preview mode");
        var sourceItem = new SelectorBarItem { Text = "Source" };
        var renderedItem = new SelectorBarItem { Text = "Rendered" };
        selector.Items.Add(sourceItem);
        selector.Items.Add(renderedItem);
        var renderedHost = new Grid { Height = previewHeight, Visibility = Visibility.Collapsed };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(renderedHost, "QuickLook.Rendered");
        Action? closeView = null;

        void Show(bool showRendered)
        {
            if (showRendered && renderedHost.Children.Count == 0)
            {
                renderedHost.Children.Add(PreviewPresenter.CreateQuickLookHtmlView(
                    PreviewHtmlRenderer.RenderDocument(path, preview), previewHeight, out var close));
                closeView = close;
            }

            renderedHost.Visibility = showRendered ? Visibility.Visible : Visibility.Collapsed;
            source.Visibility = showRendered ? Visibility.Collapsed : Visibility.Visible;
        }

        selector.SelectedItem = rendered ? renderedItem : sourceItem;
        selector.SelectionChanged += (_, _) => Show(ReferenceEquals(selector.SelectedItem, renderedItem));
        body.Children.Add(selector);
        body.Children.Add(renderedHost);
        Show(rendered);
        return () => closeView?.Invoke();
    }

    private static async Task<bool> TryAddQuickLookImageAsync(
        StackPanel body,
        FileRow row,
        FilePreview preview,
        FileOperationService fileOps,
        double maxWidth,
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
            // The image opens fitted to the lightbox; the view around it zooms, up to eight times, and scrolls.
            var view = new ScrollViewer
            {
                Height = maxHeight,
                ZoomMode = ZoomMode.Enabled,
                MinZoomFactor = (float)QuickLookZoom.Fit,
                MaxZoomFactor = (float)QuickLookZoom.Max,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = new Image
                {
                    Source = source,
                    MaxWidth = Math.Max(1, maxWidth - 24),
                    MaxHeight = Math.Max(1, maxHeight - 24),
                    Stretch = Stretch.Uniform,
                },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(view, "QuickLook.Image");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(view, row.Name);
            body.Children.Add(view);
            body.Children.Add(new TextBlock
            {
                Text = "Zoom with + and -, or Ctrl and the mouse wheel. 0 fits the image again.",
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SfTextMutedBrush"],
            });
            // ShowQuickLookAsync finds the view that zooms through the body.
            body.Tag = view;
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
            // The count starts as the dialog opens. Walking a large tree can take a while, so the one button stops
            // the walk (and starts it again once stopped or failed), and closing the dialog stops it too.
            var metricsHost = PropertiesSection(rows, "Contents");
            var metricsStatus = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("SfTextMutedBrush"),
            };
            var metricsButton = new Button { HorizontalAlignment = HorizontalAlignment.Left };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(metricsButton, "Properties.FolderCount");
            metricsHost.Children.Add(metricsButton);
            metricsHost.Children.Add(metricsStatus);

            CancellationTokenSource? metricsCts = null;
            void Show(FolderCountState state, string? error = null)
            {
                var (status, button) = FolderCountPresentation.For(state, error);
                metricsStatus.Text = status;
                metricsButton.Content = button;
            }

            void Start()
            {
                Show(FolderCountState.Counting);
                var cts = BeginUtilityOperation("PropertiesFolderMetrics");
                metricsCts = cts;
                _ = LoadFolderMetricsIntoAsync(fileOps, row.Path, metricsHost, metricsStatus, metricsButton, cts,
                    onStopped: () => { metricsCts = null; Show(FolderCountState.Stopped); },
                    onFailed: message => { metricsCts = null; Show(FolderCountState.Failed, message); });
            }

            metricsButton.Click += (_, _) =>
            {
                if (metricsCts is { } running)
                {
                    try
                    {
                        running.Cancel();
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        // The walk already finished and the registry disposed its token source.
                        metricsCts = null;
                    }
                }

                Start();
            };
            dialog.Closed += (_, _) =>
            {
                try
                {
                    metricsCts?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The lookup already finished and the registry disposed its token source.
                }
            };
            Start();
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
        Button button,
        CancellationTokenSource cts,
        Action onStopped,
        Action<string> onFailed)
    {
        try
        {
            var metrics = await fileOps.GetFolderMetricsAsync(path, cts.Token);
            if (cts.IsCancellationRequested)
            {
                onStopped();
                return;
            }

            host.Children.Remove(status);
            host.Children.Remove(button);
            PropertiesTable(host, InspectionDetails.FolderMetricRows(metrics));
        }
        catch (OperationCanceledException)
        {
            // Stopped by the button, or by the dialog closing (then nobody sees the words).
            onStopped();
        }
        catch (Exception exception)
        {
            // The button stays, so the count can be tried again.
            onFailed(exception.Message);
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
            _workspace.Clipboard.SetCut(row.Entry.Paths, systemSynced: false);
        }
        else
        {
            _workspace.Clipboard.SetCopy(row.Entry.Paths, systemSynced: false);
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
            var measured = await DialogHost.RunWithProgressAsync(
                Content.XamlRoot,
                "Folder metrics",
                $"Measuring {paths.Length} folders…",
                async (progress, token) =>
                {
                    var results = new List<KeyValuePair<string, FolderMetrics>>(paths.Length);
                    for (var index = 0; index < paths.Length; index++)
                    {
                        var path = paths[index];
                        progress.Report($"Measuring {index + 1} of {paths.Length}: {path}");
                        results.Add(new(path, await fileOps.GetFolderMetricsAsync(path, token)));
                    }

                    return results;
                },
                utilityCts.Token);
            if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
            {
                return;
            }

            SetStatusText("");
            var content = new StackPanel { Spacing = 8, Width = 520 };
            content.Children.Add(new TextBlock
            {
                Text = "Files and folders are counted through every subfolder.",
                FontSize = 12,
                Foreground = Brush("SfTextMutedBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new Border
            {
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = Brush("SfBorderBrush"),
                Background = Brush("SfBgSecondaryBrush"),
                Child = FolderComparisonGrid(InspectionDetails.FolderComparison(measured)),
            });
            var dialog = new ContentDialog
            {
                Title = "Folder metrics comparison",
                Content = new ScrollViewer
                {
                    MaxHeight = 440,
                    Padding = new Thickness(0, 0, 12, 0),
                    Content = content,
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

    /// <summary>
    /// The comparison as a table: one row per folder (its name over its path), right-aligned Files, Folders and Size
    /// columns, and the totals under a rule.
    /// </summary>
    private Grid FolderComparisonGrid(FolderComparisonTable comparison)
    {
        var table = new Grid { ColumnSpacing = 16, RowSpacing = 8 };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var column = 0; column < 3; column++)
        {
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        void Place(FrameworkElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            table.Children.Add(element);
        }

        TextBlock Cell(string text, bool header = false, bool strong = false) => new()
        {
            Text = text,
            FontSize = header ? 12 : 14,
            FontWeight = header || strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = Brush(header ? "SfTextMutedBrush" : "SfTextPrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsTextSelectionEnabled = !header,
        };

        void AddRow(FolderComparisonRow line, bool total)
        {
            var row = table.RowDefinitions.Count;
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (total)
            {
                // The rule sits in the row gap above the totals.
                var rule = new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, -5, 0, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = Brush("SfBorderBrush"),
                };
                Grid.SetColumnSpan(rule, 4);
                Place(rule, row, 0);
            }

            var name = new StackPanel { Spacing = 1 };
            name.Children.Add(new TextBlock
            {
                Text = line.Name,
                FontWeight = total ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var path = new TextBlock
            {
                Text = line.Path,
                FontSize = 11,
                Foreground = Brush("SfTextMutedBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (!total)
            {
                ToolTipService.SetToolTip(path, line.Path);
            }

            name.Children.Add(path);
            Place(name, row, 0);
            Place(Cell(line.Files, strong: total), row, 1);
            Place(Cell(line.Folders, strong: total), row, 2);
            Place(Cell(line.Size, strong: total), row, 3);
        }

        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var folderHeader = Cell("Folder", header: true);
        folderHeader.HorizontalAlignment = HorizontalAlignment.Left;
        Place(folderHeader, 0, 0);
        Place(Cell("Files", header: true), 0, 1);
        Place(Cell("Folders", header: true), 0, 2);
        Place(Cell("Size", header: true), 0, 3);
        foreach (var line in comparison.Rows)
        {
            AddRow(line, total: false);
        }

        AddRow(comparison.Total, total: true);
        return table;
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

