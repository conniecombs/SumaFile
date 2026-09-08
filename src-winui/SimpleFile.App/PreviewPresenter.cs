using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

internal sealed partial class PreviewPresenter
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
    private readonly Button _moreActionsButton;
    private readonly MenuFlyoutItem _compareMenuItem;
    private readonly MenuFlyoutItem _checksumMenuItem;
    private readonly StackPanel _optionsPanel;
    private readonly CheckBox _renderHtmlCheckBox;
    private readonly CheckBox _videoPlaybackCheckBox;
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
    private FileRow? _previewRow;
    private FilePreview? _currentPreview;
    private CancellationTokenSource? _previewCts;
    private bool _updatingPreviewOptions;
    private bool _updatingVideoFrameSelection;
    private readonly HashSet<string> _metadataKeys = new(StringComparer.OrdinalIgnoreCase);

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
        Button moreActionsButton,
        MenuFlyoutItem compareMenuItem,
        MenuFlyoutItem checksumMenuItem,
        StackPanel optionsPanel,
        CheckBox renderHtmlCheckBox,
        CheckBox videoPlaybackCheckBox,
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
        _moreActionsButton = moreActionsButton;
        _compareMenuItem = compareMenuItem;
        _checksumMenuItem = checksumMenuItem;
        _optionsPanel = optionsPanel;
        _renderHtmlCheckBox = renderHtmlCheckBox;
        _videoPlaybackCheckBox = videoPlaybackCheckBox;
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
        _renderHtmlCheckBox.Checked += OnPreviewOptionChanged;
        _renderHtmlCheckBox.Unchecked += OnPreviewOptionChanged;
        _videoPlaybackCheckBox.Checked += OnPreviewOptionChanged;
        _videoPlaybackCheckBox.Unchecked += OnPreviewOptionChanged;
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
        _previewRow = row;
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        _ = LoadAsync(row, cts);
    }

    public void Clear()
    {
        _previewPath = null;
        _previewRow = null;
        _currentPreview = null;
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
        ResetPreviewOptions();
        _textBox.Text = "";
        _textBox.Visibility = Visibility.Collapsed;
        _emptyText.Text = "No preview loaded.";
        _emptyText.Visibility = Visibility.Visible;
        ClearMetadataRows();
        _checksumText.Text = "";
        UpdateButtons(null);
    }

    public void CancelPending()
    {
        _previewPath = null;
        _previewRow = null;
        _currentPreview = null;
        _previewCts?.Cancel();
        _previewCts = null;
        _ = Interlocked.Increment(ref _previewToken);
        ClearPathBackedPreviews();
        HideVideoFrameControls();
        ResetPreviewOptions();
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
        _checksumMenuItem.IsEnabled = canInspectFile;
        _compareMenuItem.IsEnabled = _compareButton.IsEnabled;
        _moreActionsButton.IsEnabled = _checksumMenuItem.IsEnabled || _compareMenuItem.IsEnabled;
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
            _previewRow = row;
            _currentPreview = null;
            _image.Source = null;
            _image.Visibility = Visibility.Collapsed;
            ClearPathBackedPreviews();
            HideVideoFrameControls();
            ResetPreviewOptions();
            _textBox.Text = "";
            _textBox.Visibility = Visibility.Collapsed;
            _emptyText.Text = row.IsDir ? "Folder selected." : "Loading preview...";
            _emptyText.Visibility = Visibility.Visible;
            ClearMetadataRows();
            _checksumText.Text = "";
            AddMetadataSection("Selection");
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

                _currentPreview = preview;
                ConfigurePreviewOptions(row, preview);
                AddMetadataSection("Preview");
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

    private void ConfigurePreviewOptions(FileRow row, FilePreview preview)
    {
        var settings = _workspace()?.Settings ?? UiSettings.CreateDefault();
        var canRenderHtml = PreviewCapabilities.CanRenderAsHtml(row.Path, preview);
        var canPreviewVideo = PreviewCapabilities.IsVideo(preview);

        _updatingPreviewOptions = true;
        try
        {
            _renderHtmlCheckBox.Visibility = canRenderHtml ? Visibility.Visible : Visibility.Collapsed;
            _renderHtmlCheckBox.IsChecked = canRenderHtml && settings.PreviewRenderHtml;
            _videoPlaybackCheckBox.Visibility = canPreviewVideo ? Visibility.Visible : Visibility.Collapsed;
            _videoPlaybackCheckBox.IsChecked = canPreviewVideo && settings.PreviewVideoPlaybackEnabled;
            _optionsPanel.Visibility = canRenderHtml || canPreviewVideo ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _updatingPreviewOptions = false;
        }
    }

    private void ResetPreviewOptions()
    {
        _updatingPreviewOptions = true;
        try
        {
            _renderHtmlCheckBox.IsChecked = false;
            _renderHtmlCheckBox.Visibility = Visibility.Collapsed;
            _videoPlaybackCheckBox.IsChecked = false;
            _videoPlaybackCheckBox.Visibility = Visibility.Collapsed;
            _optionsPanel.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _updatingPreviewOptions = false;
        }
    }

    private async void OnPreviewOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingPreviewOptions || _currentPreview is null || _previewRow is null)
        {
            return;
        }

        var workspace = _workspace();
        if (workspace is null)
        {
            return;
        }

        if (ReferenceEquals(sender, _renderHtmlCheckBox))
        {
            workspace.Settings.PreviewRenderHtml = _renderHtmlCheckBox.IsChecked == true;
        }
        else if (ReferenceEquals(sender, _videoPlaybackCheckBox))
        {
            workspace.Settings.PreviewVideoPlaybackEnabled = _videoPlaybackCheckBox.IsChecked == true;
        }

        try
        {
            await workspace.SaveUiSettingsAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _showMessage("Preview settings", exception.Message, InfoBarSeverity.Error);
        }

        ReloadCurrentPreview();
    }

    private void ReloadCurrentPreview()
    {
        if (_previewRow is not { } row)
        {
            return;
        }

        _previewPath = null;
        Queue(row);
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

}
