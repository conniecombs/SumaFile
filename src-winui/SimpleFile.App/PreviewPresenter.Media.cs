using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Windows.Media.Core;

namespace SimpleFile.App;

internal sealed partial class PreviewPresenter
{
    private MediaSource? _mediaSource;

    /// <summary>
    /// Stops playback and drops path-backed sources (media, PDF) so a hidden pane neither keeps
    /// playing audio nor holds the file open. The next selection or re-show reloads the preview.
    /// </summary>
    public void ReleasePathBackedPreviews() => CancelPending();

    private void ReleaseMediaSource()
    {
        try
        {
            _mediaPlayer.MediaPlayer?.Pause();
        }
        catch
        {
            // Pausing a player with no media throws on some stacks; releasing still proceeds.
        }

        _mediaPlayer.Source = null;
        _mediaSource?.Dispose();
        _mediaSource = null;
    }

    private async Task<bool> TryRenderPdfPreviewAsync(string path, int token, CancellationToken cancellationToken)
    {
        if (!PreviewPathSupport.CanUsePathBackedPreview(path, "pdf") || !IsCurrent(path, token, cancellationToken))
        {
            return false;
        }

        try
        {
            ClearIcon();
            // Not just "Source = null": the media source holds the file open until it is disposed.
            ReleaseMediaSource();
            _mediaPlayer.Visibility = Visibility.Collapsed;
            var source = new Uri(path);
            // The built-in PDF viewer needs script; navigation stays pinned to this file.
            await EnsurePreviewWebViewAsync(allowScript: true, allowedNavigation: source);
            if (!IsCurrent(path, token, cancellationToken))
            {
                return true;
            }

            _pdfView.Source = source;
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
            ReleaseMediaSource();
            _mediaSource = MediaSource.CreateFromUri(new Uri(path));
            _mediaPlayer.Source = _mediaSource;
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
            ReleaseMediaSource();
            _mediaPlayer.Visibility = Visibility.Collapsed;
            HideVideoFrameControls();
            return false;
        }
    }

    private void ClearPathBackedPreviews()
    {
        _pdfView.Source = null;
        _pdfView.Visibility = Visibility.Collapsed;
        ReleaseMediaSource();
        _mediaPlayer.Visibility = Visibility.Collapsed;
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

        MediaSource? mediaSource = null;
        try
        {
            if (string.Equals(preview.FileType, "pdf", StringComparison.OrdinalIgnoreCase))
            {
                element = CreateQuickLookPdfView(new Uri(row.Path), height, out cleanup);
                return true;
            }

            if (PreviewPathSupport.IsMediaPreviewType(preview.FileType))
            {
                mediaSource = MediaSource.CreateFromUri(new Uri(row.Path));
                var player = new MediaPlayerElement
                {
                    Source = mediaSource,
                    Height = string.Equals(preview.FileType, "audio", StringComparison.OrdinalIgnoreCase)
                        ? 112
                        : height,
                    AreTransportControlsEnabled = true,
                    AutoPlay = false,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                element = player;
                var source = mediaSource;
                cleanup = () => ReleaseQuickLookMedia(player, source);
                return true;
            }
        }
        catch
        {
            mediaSource?.Dispose();
            element = null;
            cleanup = null;
        }

        return false;
    }

    /// <summary>
    /// Stops a Quick Look player and disposes its source. Clearing the element's source alone
    /// leaves the media source alive, and the file stays locked until it is collected.
    /// </summary>
    private static void ReleaseQuickLookMedia(MediaPlayerElement player, MediaSource source)
    {
        try
        {
            player.MediaPlayer?.Pause();
        }
        catch
        {
            // Pausing a player with no media throws on some stacks; releasing still proceeds.
        }

        try
        {
            player.Source = null;
        }
        catch
        {
            // The element may already be torn down with its dialog.
        }

        source.Dispose();
    }
}
