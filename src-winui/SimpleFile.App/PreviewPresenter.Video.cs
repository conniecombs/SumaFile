using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;

namespace SimpleFile.App;

internal sealed partial class PreviewPresenter
{
    private bool TryRenderVideoPosterPreview(string path, int token, CancellationToken cancellationToken)
    {
        if (!VideoThumbnailExtractor.CanUseVideoThumbnail(path) || !IsCurrent(path, token, cancellationToken))
        {
            return false;
        }

        ClearIcon();
        _emptyText.Visibility = Visibility.Collapsed;
        ShowVideoFrameControls(path, token, cancellationToken);
        return true;
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
}
