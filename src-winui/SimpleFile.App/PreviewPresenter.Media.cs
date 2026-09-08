using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Windows.Media.Core;

namespace SimpleFile.App;

internal sealed partial class PreviewPresenter
{
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

    private void ClearPathBackedPreviews()
    {
        _pdfView.Source = null;
        _pdfView.Visibility = Visibility.Collapsed;
        _mediaPlayer.Source = null;
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
}
