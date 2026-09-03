using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
using SimpleFile.Ipc;
using System.Globalization;
using Windows.Media.Core;

namespace SimpleFile.App;

internal sealed class PreviewPresenter
{
    private readonly Func<ExplorerWorkspace?> _workspace;
    private readonly Func<FileRow?> _activeSelectedRow;
    private readonly Func<IReadOnlyList<FileRow>> _activeSelectedRows;
    private readonly Func<XamlRoot> _xamlRoot;
    private readonly Func<CancellationTokenSource> _beginUtilityOperation;
    private readonly Action<CancellationTokenSource> _finishUtilityOperation;
    private readonly Func<Task> _openWithSelectedAsync;
    private readonly Action<string, string, InfoBarSeverity> _showMessage;

    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly Button _openButton;
    private readonly Button _openWithButton;
    private readonly Button _revealButton;
    private readonly Button _compareButton;
    private readonly Button _checksumButton;
    private readonly StackPanel _iconPanel;
    private readonly Image _iconImage;
    private readonly TextBlock _iconLabel;
    private readonly Image _image;
    private readonly WebView2 _pdfView;
    private readonly MediaPlayerElement _mediaPlayer;
    private readonly StackPanel _videoFrameControls;
    private readonly Image _videoFrameImage;
    private readonly RadioButtons _videoFramePresets;
    private readonly TextBox _textBox;
    private readonly TextBlock _emptyText;
    private readonly StackPanel _metadataRows;
    private readonly TextBlock _checksumText;

    private int _previewToken;
    private int _videoFrameToken;
    private string? _previewPath;
    private CancellationTokenSource? _previewCts;
    private bool _updatingVideoFrameSelection;

    public PreviewPresenter(
        Func<ExplorerWorkspace?> workspace,
        Func<FileRow?> activeSelectedRow,
        Func<IReadOnlyList<FileRow>> activeSelectedRows,
        Func<XamlRoot> xamlRoot,
        Func<CancellationTokenSource> beginUtilityOperation,
        Action<CancellationTokenSource> finishUtilityOperation,
        Func<Task> openWithSelectedAsync,
        Action<string, string, InfoBarSeverity> showMessage,
        TextBlock title,
        TextBlock subtitle,
        Button openButton,
        Button openWithButton,
        Button revealButton,
        Button compareButton,
        Button checksumButton,
        StackPanel iconPanel,
        Image iconImage,
        TextBlock iconLabel,
        Image image,
        WebView2 pdfView,
        MediaPlayerElement mediaPlayer,
        StackPanel videoFrameControls,
        Image videoFrameImage,
        RadioButtons videoFramePresets,
        TextBox textBox,
        TextBlock emptyText,
        StackPanel metadataRows,
        TextBlock checksumText)
    {
        _workspace = workspace;
        _activeSelectedRow = activeSelectedRow;
        _activeSelectedRows = activeSelectedRows;
        _xamlRoot = xamlRoot;
        _beginUtilityOperation = beginUtilityOperation;
        _finishUtilityOperation = finishUtilityOperation;
        _openWithSelectedAsync = openWithSelectedAsync;
        _showMessage = showMessage;
        _title = title;
        _subtitle = subtitle;
        _openButton = openButton;
        _openWithButton = openWithButton;
        _revealButton = revealButton;
        _compareButton = compareButton;
        _checksumButton = checksumButton;
        _iconPanel = iconPanel;
        _iconImage = iconImage;
        _iconLabel = iconLabel;
        _image = image;
        _pdfView = pdfView;
        _mediaPlayer = mediaPlayer;
        _videoFrameControls = videoFrameControls;
        _videoFrameImage = videoFrameImage;
        _videoFramePresets = videoFramePresets;
        _textBox = textBox;
        _emptyText = emptyText;
        _metadataRows = metadataRows;
        _checksumText = checksumText;
        _videoFramePresets.SelectionChanged += OnVideoFramePresetChanged;
    }

    public string? CurrentPath => _previewPath;

    public void QueueFromSelection()
    {
        var row = _activeSelectedRow();
        if (row is null)
        {
            Clear();
            return;
        }

        Queue(row);
    }

    public void Queue(FileRow row)
    {
        UpdateButtons(row);
        if (string.Equals(_previewPath, row.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _previewPath = row.Path;
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        _ = LoadAsync(row, cts);
    }

    public void Clear()
    {
        _previewPath = null;
        _previewCts?.Cancel();
        _previewCts = null;
        _ = Interlocked.Increment(ref _previewToken);
        _title.Text = "Preview";
        _subtitle.Text = "Select a file";
        ClearIcon();
        _image.Source = null;
        _image.Visibility = Visibility.Collapsed;
        ClearPathBackedPreviews();
        HideVideoFrameControls();
        _textBox.Text = "";
        _textBox.Visibility = Visibility.Collapsed;
        _emptyText.Text = "No preview loaded.";
        _emptyText.Visibility = Visibility.Visible;
        _metadataRows.Children.Clear();
        _checksumText.Text = "";
        UpdateButtons(null);
    }

    public void CancelPending()
    {
        _previewPath = null;
        _previewCts?.Cancel();
        _previewCts = null;
        _ = Interlocked.Increment(ref _previewToken);
        ClearPathBackedPreviews();
        HideVideoFrameControls();
    }

    public void UpdateButtons(FileRow? row)
    {
        var selected = _activeSelectedRows();
        var canActOnSelection = row is not null;
        var canInspectFile = row is not null && !row.IsDir;
        _openButton.IsEnabled = canActOnSelection;
        _revealButton.IsEnabled = canActOnSelection;
        _openWithButton.IsEnabled = canInspectFile;
        _checksumButton.IsEnabled = canInspectFile;
        _compareButton.IsEnabled = selected.Count == 2 && selected.All(item => !item.IsDir);
    }

    public async Task OpenSelectedAsync()
    {
        var workspace = _workspace();
        if (workspace is null || _activeSelectedRow() is not { } row)
        {
            return;
        }

        var pane = workspace.ActivePane;
        var utilityCts = _beginUtilityOperation();
        try
        {
            await workspace.OpenPathAsync(row.Path, row.IsDir, pane, utilityCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _showMessage("Open", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _finishUtilityOperation(utilityCts);
        }
    }

    public async Task RevealSelectedAsync()
    {
        var workspace = _workspace();
        if (workspace is null || _activeSelectedRow() is not { } row)
        {
            return;
        }

        var utilityCts = _beginUtilityOperation();
        try
        {
            await workspace.RevealInFolderAsync(row.Path, utilityCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _showMessage("Reveal in folder", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _finishUtilityOperation(utilityCts);
        }
    }

    public Task OpenWithSelectedAsync() => _openWithSelectedAsync();

    public async Task ComputeChecksumAsync()
    {
        var workspace = _workspace();
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null || _activeSelectedRow() is not { IsDir: false } row)
        {
            return;
        }

        _checksumButton.IsEnabled = false;
        _checksumText.Text = "Computing...";
        var token = _previewToken;
        var path = row.Path;
        var utilityCts = _beginUtilityOperation();
        try
        {
            var checksums = await fileOps.ComputeChecksumAsync(path, utilityCts.Token);
            if (!ReferenceEquals(_workspace(), workspace)
                || utilityCts.IsCancellationRequested
                || !IsCurrent(path, token))
            {
                return;
            }

            _checksumText.Text = InspectionDetails.ChecksumsText(checksums);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(path, token))
            {
                _checksumText.Text = "";
            }
        }
        catch (Exception exception)
        {
            if (IsCurrent(path, token))
            {
                _checksumText.Text = exception.Message;
            }
        }
        finally
        {
            if (IsCurrent(path, token))
            {
                _checksumButton.IsEnabled = _activeSelectedRow() is { IsDir: false };
            }

            _finishUtilityOperation(utilityCts);
        }
    }

    public async Task CompareSelectedFilesAsync()
    {
        var workspace = _workspace();
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null)
        {
            return;
        }

        var selected = _activeSelectedRows();
        if (selected.Count != 2 || selected.Any(row => row.IsDir))
        {
            return;
        }

        var pathA = selected[0].Path;
        var pathB = selected[1].Path;
        var utilityCts = _beginUtilityOperation();
        try
        {
            var comparison = await fileOps.CompareFilesAsync(pathA, pathB, utilityCts.Token);
            if (!ReferenceEquals(_workspace(), workspace) || utilityCts.IsCancellationRequested)
            {
                return;
            }

            await ShowComparisonAsync(comparison);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _showMessage("Compare files", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _finishUtilityOperation(utilityCts);
        }
    }

    private async Task LoadAsync(FileRow row, CancellationTokenSource cts)
    {
        var token = Interlocked.Increment(ref _previewToken);
        var cancellationToken = cts.Token;
        try
        {
            _title.Text = row.Name;
            _subtitle.Text = row.Path;
            ShowIcon(row);
            _image.Source = null;
            _image.Visibility = Visibility.Collapsed;
            ClearPathBackedPreviews();
            HideVideoFrameControls();
            _textBox.Text = "";
            _textBox.Visibility = Visibility.Collapsed;
            _emptyText.Text = row.IsDir ? "Folder selected." : "Loading preview...";
            _emptyText.Visibility = Visibility.Visible;
            _metadataRows.Children.Clear();
            _checksumText.Text = "";
            AddMetadataRows(InspectionDetails.PreviewSelectionRows(row));

            if (row.IsDir || _workspace()?.FileOps is null)
            {
                return;
            }

            FilePreview? preview = null;
            try
            {
                preview = await _workspace()!.FileOps!.ReadFilePreviewAsync(row.Path, 2_000_000, cancellationToken);
                if (!IsCurrent(row.Path, token, cancellationToken))
                {
                    return;
                }

                AddMetadataRows(InspectionDetails.PreviewRows(preview));
                await RenderContentAsync(row, preview, token, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (!IsCurrent(row.Path, token, cancellationToken))
                {
                    return;
                }

                _emptyText.Text = exception.Message;
            }

            await LoadMetadataAsync(row.Path, preview?.FileType, token, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cts))
            {
                _previewCts = null;
            }

            cts.Dispose();
        }
    }

    private async Task RenderContentAsync(FileRow row, FilePreview preview, int token, CancellationToken cancellationToken)
    {
        var path = row.Path;
        if (preview.FileType == "text" && preview.Content is not null)
        {
            if (!IsCurrent(path, token, cancellationToken))
            {
                return;
            }

            ClearIcon();
            _textBox.Text = preview.Content;
            _textBox.Visibility = Visibility.Visible;
            _emptyText.Visibility = Visibility.Collapsed;
            return;
        }

        if (preview.FileType == "image")
        {
            if (preview.Content is not null && await TrySetImageAsync(preview.Content, path, token, cancellationToken))
            {
                ClearIcon();
                _emptyText.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var thumbnail = await _workspace()!.FileOps!.GenerateThumbnailAsync(path, 256, cancellationToken);
                if (await TrySetImageAsync(thumbnail, path, token, cancellationToken))
                {
                    ClearIcon();
                    _emptyText.Text = "Thumbnail preview";
                    return;
                }
            }
            catch
            {
                // Unsupported image codecs still keep metadata and actions visible.
            }
        }

        if (PreviewPathSupport.IsPdfPreviewType(preview.FileType) && TryRenderPdfPreview(path, token, cancellationToken))
        {
            return;
        }

        if (PreviewPathSupport.IsMediaPreviewType(preview.FileType)
            && TryRenderMediaPreview(path, preview.FileType, token, cancellationToken))
        {
            return;
        }

        if (!IsCurrent(path, token, cancellationToken))
        {
            return;
        }

        ShowIcon(row, FileTypePreviewLabel(row, preview));
        _emptyText.Text = IconPreviewMessage(preview);
        _emptyText.Visibility = Visibility.Visible;
    }

    private async Task LoadMetadataAsync(string path, string? previewType, int token, CancellationToken cancellationToken)
    {
        if (_workspace()?.FileOps is null)
        {
            return;
        }

        try
        {
            var metadata = await _workspace()!.FileOps!.GetFileMetadataAsync(path, cancellationToken);
            if (!IsCurrent(path, token, cancellationToken))
            {
                return;
            }

            AddMetadataRows(InspectionDetails.MetadataRows(metadata, includeSummary: true, includeKind: true));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (IsCurrent(path, token, cancellationToken))
            {
                AddMetadataRow("Metadata", exception.Message);
            }
        }

        if (!string.Equals(previewType, "image", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var image = await _workspace()!.FileOps!.GetImageMetadataAsync(path, cancellationToken);
            if (!IsCurrent(path, token, cancellationToken))
            {
                return;
            }

            AddMetadataRow("Dimensions", $"{image.Width} x {image.Height}");
            AddMetadataRows(InspectionDetails.RawRows(image.Exif.Take(12)));
        }
        catch
        {
            // get_file_metadata already covers the non-EXIF image summary.
        }
    }

    private async Task<bool> TrySetImageAsync(string base64, string path, int token, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsCurrent(path, token, cancellationToken))
            {
                return false;
            }

            var source = await PreviewImageSourceFactory.FromBase64Async(base64, path);

            if (!IsCurrent(path, token, cancellationToken))
            {
                return false;
            }

            _image.Source = source;
            _image.Visibility = Visibility.Visible;
            return true;
        }
        catch
        {
            _image.Source = null;
            _image.Visibility = Visibility.Collapsed;
            return false;
        }
    }

    private bool TryRenderPdfPreview(string path, int token, CancellationToken cancellationToken)
    {
        if (!PreviewPathSupport.CanUsePathBackedPreview(path, "pdf") || !IsCurrent(path, token, cancellationToken))
        {
            return false;
        }

        try
        {
            ClearIcon();
            _mediaPlayer.Source = null;
            _mediaPlayer.Visibility = Visibility.Collapsed;
            _pdfView.Source = new Uri(path);
            _pdfView.Visibility = Visibility.Visible;
            _emptyText.Visibility = Visibility.Collapsed;
            return true;
        }
        catch
        {
            _pdfView.Source = null;
            _pdfView.Visibility = Visibility.Collapsed;
            return false;
        }
    }

    private bool TryRenderMediaPreview(string path, string fileType, int token, CancellationToken cancellationToken)
    {
        if (!PreviewPathSupport.CanUsePathBackedPreview(path, fileType) || !IsCurrent(path, token, cancellationToken))
        {
            return false;
        }

        try
        {
            ClearIcon();
            _pdfView.Source = null;
            _pdfView.Visibility = Visibility.Collapsed;
            _mediaPlayer.Height = string.Equals(fileType, "audio", StringComparison.OrdinalIgnoreCase) ? 96 : 220;
            _mediaPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
            _mediaPlayer.Visibility = Visibility.Visible;
            if (string.Equals(fileType, "video", StringComparison.OrdinalIgnoreCase))
            {
                ShowVideoFrameControls(path, token, cancellationToken);
            }
            else
            {
                HideVideoFrameControls();
            }

            _emptyText.Visibility = Visibility.Collapsed;
            return true;
        }
        catch
        {
            _mediaPlayer.Source = null;
            _mediaPlayer.Visibility = Visibility.Collapsed;
            HideVideoFrameControls();
            return false;
        }
    }

    private void ShowVideoFrameControls(string path, int token, CancellationToken cancellationToken)
    {
        if (!VideoThumbnailExtractor.CanUseVideoThumbnail(path) || !IsCurrent(path, token, cancellationToken))
        {
            HideVideoFrameControls();
            return;
        }

        var frame = FileListThumbnailHost.VideoFrameForPath(path);
        _videoFrameControls.Visibility = Visibility.Visible;
        _videoFrameImage.Source = ShellIconLoader.ForEntry(path, isDirectory: false, 72);
        SelectVideoFramePreset(frame);
        _ = LoadVideoFramePreviewAsync(path, frame, token, cancellationToken);
    }

    private async Task LoadVideoFramePreviewAsync(
        string path,
        VideoThumbnailFrame frame,
        int previewToken,
        CancellationToken cancellationToken)
    {
        var frameToken = Interlocked.Increment(ref _videoFrameToken);
        try
        {
            var source = await VideoThumbnailExtractor.LoadAsync(path, 144, frame);
            if (source is null || !IsCurrentVideoFrame(path, previewToken, frame, frameToken, cancellationToken))
            {
                return;
            }

            _videoFrameImage.Source = source;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool IsCurrentVideoFrame(
        string path,
        int previewToken,
        VideoThumbnailFrame frame,
        int frameToken,
        CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && IsCurrent(path, previewToken)
            && frameToken == _videoFrameToken
            && FileListThumbnailHost.VideoFrameForPath(path).Percent == frame.Percent;
    }

    private void OnVideoFramePresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingVideoFrameSelection || _previewPath is not { } path)
        {
            return;
        }

        if (!TrySelectedVideoFrame(out var frame) || !VideoThumbnailExtractor.CanUseVideoThumbnail(path))
        {
            return;
        }

        FileListThumbnailHost.SetVideoFramePreference(path, frame);
        _videoFrameImage.Source = ShellIconLoader.ForEntry(path, isDirectory: false, 72);
        var cancellationToken = _previewCts?.Token ?? CancellationToken.None;
        _ = LoadVideoFramePreviewAsync(path, frame, _previewToken, cancellationToken);
    }

    private void SelectVideoFramePreset(VideoThumbnailFrame frame)
    {
        _updatingVideoFrameSelection = true;
        try
        {
            for (var index = 0; index < _videoFramePresets.Items.Count; index++)
            {
                if (_videoFramePresets.Items[index] is RadioButton item
                    && TryReadVideoFrame(item, out var itemFrame)
                    && itemFrame.Percent == frame.Percent)
                {
                    _videoFramePresets.SelectedIndex = index;
                    return;
                }
            }

            _videoFramePresets.SelectedIndex = -1;
        }
        finally
        {
            _updatingVideoFrameSelection = false;
        }
    }

    private bool TrySelectedVideoFrame(out VideoThumbnailFrame frame)
    {
        if (_videoFramePresets.SelectedItem is RadioButton item
            && TryReadVideoFrame(item, out frame))
        {
            return true;
        }

        frame = VideoThumbnailFrame.Default;
        return false;
    }

    private static bool TryReadVideoFrame(RadioButton item, out VideoThumbnailFrame frame)
    {
        if (item.Tag is string tag
            && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
        {
            frame = new VideoThumbnailFrame(percent);
            return true;
        }

        frame = VideoThumbnailFrame.Default;
        return false;
    }

    private void HideVideoFrameControls()
    {
        _ = Interlocked.Increment(ref _videoFrameToken);
        _videoFrameControls.Visibility = Visibility.Collapsed;
        _videoFrameImage.Source = null;
        _updatingVideoFrameSelection = true;
        try
        {
            _videoFramePresets.SelectedIndex = -1;
        }
        finally
        {
            _updatingVideoFrameSelection = false;
        }
    }

    private void ClearPathBackedPreviews()
    {
        _pdfView.Source = null;
        _pdfView.Visibility = Visibility.Collapsed;
        _mediaPlayer.Source = null;
        _mediaPlayer.Visibility = Visibility.Collapsed;
    }

    private bool IsCurrent(string path, int token)
    {
        return token == _previewToken
            && string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCurrent(string path, int token, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested && IsCurrent(path, token);
    }

    private void ShowIcon(FileRow row, string? label = null)
    {
        _iconImage.Source = ShellIconLoader.ForEntry(row.Path, row.IsDir, 96);
        _iconLabel.Text = string.IsNullOrWhiteSpace(label) ? row.TypeText : label;
        _iconPanel.Visibility = Visibility.Visible;
    }

    private void ClearIcon()
    {
        _iconImage.Source = null;
        _iconLabel.Text = "";
        _iconPanel.Visibility = Visibility.Collapsed;
    }

    private void AddMetadataRows(IEnumerable<InspectionDetailRow> rows)
    {
        foreach (var row in rows)
        {
            AddMetadataRow(row.Label, row.Value);
        }
    }

    private void AddMetadataRow(string label, string value)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var row = new Grid
        {
            ColumnSpacing = 10,
            Margin = new Thickness(0, 0, 0, 1),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Brush("SfTextMutedBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 12,
            Foreground = Brush("SfTextPrimaryBrush"),
            Opacity = 0.88,
            TextWrapping = TextWrapping.Wrap,
        };

        Grid.SetColumn(valueText, 1);
        row.Children.Add(labelText);
        row.Children.Add(valueText);
        _metadataRows.Children.Add(row);
    }

    private async Task ShowComparisonAsync(FileComparison comparison)
    {
        var isBinary = string.Equals(comparison.ComparisonType, "binary", StringComparison.OrdinalIgnoreCase);
        var summary = isBinary
            ? BinaryComparisonSummary(comparison)
            : TextComparisonSummary(comparison);
        var rows = isBinary
            ? BinaryComparisonRows(comparison)
            : TextComparisonRows(comparison);

        var diffBox = new TextBox
        {
            Text = string.Join(Environment.NewLine, rows),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MaxHeight = 360,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(diffBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(diffBox, ScrollBarVisibility.Auto);

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = $"{comparison.LeftName} -> {comparison.RightName}" },
                new TextBlock { Text = summary },
                diffBox,
            },
        };

        var dialog = new ContentDialog
        {
            Title = "File Compare",
            Content = body,
            CloseButtonText = "Close",
            XamlRoot = _xamlRoot(),
        };

        await dialog.ShowAsync();
    }

    private static string TextComparisonSummary(FileComparison comparison)
    {
        return comparison.Identical
            ? "Files are identical."
            : $"{comparison.Added} added, {comparison.Removed} removed, {comparison.Changed} changed";
    }

    private static IEnumerable<string> TextComparisonRows(FileComparison comparison)
    {
        return comparison.Rows
            .Take(80)
            .Select(row =>
            {
                var left = row.LeftLine?.ToString(CultureInfo.CurrentCulture) ?? "";
                var right = row.RightLine?.ToString(CultureInfo.CurrentCulture) ?? "";
                var text = row.LeftText ?? row.RightText ?? "";
                return $"{row.Kind,-8} {left,4} {right,4}  {text}";
            });
    }

    private static string BinaryComparisonSummary(FileComparison comparison)
    {
        if (comparison.Identical)
        {
            return $"Binary files are identical ({FormatByteCount(comparison.ComparedBytes ?? comparison.LeftSize)} compared).";
        }

        var first = comparison.FirstDifference is { } offset
            ? $"first difference at 0x{offset:X}"
            : "first difference unavailable";
        var differences = comparison.DifferentBytes is { } differentBytes
            ? FormatByteCount(differentBytes)
            : "one or more bytes";
        var suffix = comparison.BinaryRowsTruncated
            ? $" Showing first {comparison.BinaryRows.Count.ToString(CultureInfo.CurrentCulture)} differing rows."
            : "";
        return $"Binary files differ: {differences} differ, {first}.{suffix}";
    }

    private static IEnumerable<string> BinaryComparisonRows(FileComparison comparison)
    {
        yield return "Offset(h)    Left hex                                           Right hex                                          Left ASCII        Right ASCII";
        foreach (var row in comparison.BinaryRows.Take(128))
        {
            yield return $"{row.Offset,10:X}  {row.LeftHex,-47}  {row.RightHex,-47}  {row.LeftAscii}  {row.RightAscii}";
        }
    }

    private static string FormatByteCount(ulong bytes)
    {
        return bytes == 1
            ? "1 byte"
            : $"{bytes.ToString("N0", CultureInfo.CurrentCulture)} bytes";
    }

    public static Image CreateFileTypePreviewIcon(FileRow row, int iconSize)
    {
        return new Image
        {
            Width = iconSize,
            Height = iconSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            Stretch = Stretch.Uniform,
            Source = ShellIconLoader.ForEntry(row.Path, row.IsDir, iconSize),
        };
    }

    public static bool TryCreatePathBackedPreview(
        FileRow row,
        FilePreview preview,
        double height,
        out FrameworkElement? element,
        out Action? cleanup)
    {
        element = null;
        cleanup = null;
        if (!PreviewPathSupport.CanUsePathBackedPreview(row.Path, preview.FileType))
        {
            return false;
        }

        try
        {
            if (string.Equals(preview.FileType, "pdf", StringComparison.OrdinalIgnoreCase))
            {
                var view = new WebView2
                {
                    Source = new Uri(row.Path),
                    Height = height,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                element = view;
                cleanup = () => view.Source = null;
                return true;
            }

            if (PreviewPathSupport.IsMediaPreviewType(preview.FileType))
            {
                var player = new MediaPlayerElement
                {
                    Source = MediaSource.CreateFromUri(new Uri(row.Path)),
                    Height = string.Equals(preview.FileType, "audio", StringComparison.OrdinalIgnoreCase)
                        ? 112
                        : height,
                    AreTransportControlsEnabled = true,
                    AutoPlay = false,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                element = player;
                cleanup = () => player.Source = null;
                return true;
            }
        }
        catch
        {
            element = null;
            cleanup = null;
        }

        return false;
    }

    private static string FileTypePreviewLabel(FileRow row, FilePreview preview)
    {
        if (!string.IsNullOrWhiteSpace(row.TypeText))
        {
            return row.TypeText;
        }

        return preview.FileType switch
        {
            "audio" => "Audio file",
            "video" => "Video file",
            "image" => "Image file",
            "pdf" => "PDF file",
            "document" => "Document",
            "spreadsheet" => "Spreadsheet",
            "presentation" => "Presentation",
            "archive" => "Archive",
            "package" => "Package",
            "executable" => "Application",
            "font" => "Font file",
            "database" => "Database file",
            "disk-image" => "Disk image",
            "ebook" => "Ebook",
            "email" => "Email",
            "calendar" => "Calendar file",
            "contact" => "Contact file",
            "certificate" => "Certificate",
            "design" => "Design file",
            "model" => "3D model",
            "cad" => "CAD file",
            "torrent" => "Torrent file",
            "binary" => "Binary file",
            _ => "File",
        };
    }

    public static string IconPreviewMessage(FilePreview preview)
    {
        return preview.FileType switch
        {
            "pdf" => "Showing the file-type icon for this PDF.",
            "image" => "Showing the file-type icon for this image.",
            "audio" => "Showing the file-type icon for this audio file.",
            "video" => "Showing the file-type icon for this video file.",
            "document" => "Showing the file-type icon for this document.",
            "spreadsheet" => "Showing the file-type icon for this spreadsheet.",
            "presentation" => "Showing the file-type icon for this presentation.",
            "archive" => "Showing the file-type icon for this archive.",
            "package" => "Showing the file-type icon for this package.",
            "executable" => "Showing the file-type icon for this application or script.",
            "font" => "Showing the file-type icon for this font.",
            "database" => "Showing the file-type icon for this database file.",
            "disk-image" => "Showing the file-type icon for this disk image.",
            "ebook" => "Showing the file-type icon for this ebook.",
            "email" => "Showing the file-type icon for this email file.",
            "calendar" => "Showing the file-type icon for this calendar file.",
            "contact" => "Showing the file-type icon for this contact file.",
            "certificate" => "Showing the file-type icon for this certificate or key.",
            "design" => "Showing the file-type icon for this design file.",
            "model" => "Showing the file-type icon for this 3D model.",
            "cad" => "Showing the file-type icon for this CAD file.",
            "torrent" => "Showing the file-type icon for this torrent file.",
            "binary" => "Showing the file-type icon for this binary file.",
            _ => "Showing the file-type icon for this file.",
        };
    }

    public void RefreshThemeResources()
    {
        foreach (var row in _metadataRows.Children.OfType<Grid>())
        {
            foreach (var text in row.Children.OfType<TextBlock>())
            {
                text.Foreground = Brush(Grid.GetColumn(text) == 0 ? "SfTextMutedBrush" : "SfTextPrimaryBrush");
            }
        }
    }

    private Brush Brush(string key)
    {
        return ThemeResourceLookup.Brush(_metadataRows, key);
    }
}
