using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

internal sealed partial class PreviewPresenter
{
    private bool TryRenderHtmlPreview(
        string path,
        FilePreview preview,
        int token,
        CancellationToken cancellationToken)
    {
        if (!PreviewCapabilities.CanRenderAsHtml(path, preview) || preview.Content is null)
        {
            return false;
        }

        if (!IsCurrent(path, token, cancellationToken))
        {
            return true;
        }

        try
        {
            ClearIcon();
            _textBox.Text = "";
            _textBox.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            _mediaPlayer.Source = null;
            _mediaPlayer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            HideVideoFrameControls();
            _pdfView.NavigateToString(PreviewHtmlRenderer.RenderDocument(path, preview));
            _pdfView.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            _emptyText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            return true;
        }
        catch
        {
            _pdfView.Source = null;
            _pdfView.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            return false;
        }
    }
}
