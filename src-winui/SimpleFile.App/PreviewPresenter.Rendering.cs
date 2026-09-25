using Microsoft.UI.Xaml;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

internal sealed partial class PreviewPresenter
{
    private async Task RenderContentAsync(FileRow row, FilePreview preview, int token, CancellationToken cancellationToken)
    {
        var path = row.Path;
        var settings = _workspace()?.Settings ?? UiSettings.CreateDefault();
        if (PreviewCapabilities.ShouldRenderHtml(settings, path, preview)
            && await TryRenderHtmlPreviewAsync(path, preview, token, cancellationToken))
        {
            return;
        }

        if (TryRenderTextPreview(path, preview, token, cancellationToken))
        {
            return;
        }

        if (await TryRenderImagePreviewAsync(row, preview, token, cancellationToken))
        {
            return;
        }

        if (PreviewPathSupport.IsPdfPreviewType(preview.FileType) && TryRenderPdfPreview(path, token, cancellationToken))
        {
            return;
        }

        if (PreviewCapabilities.IsVideo(preview) && !PreviewCapabilities.ShouldPreviewVideo(settings, preview))
        {
            if (TryRenderVideoPosterPreview(path, token, cancellationToken))
            {
                return;
            }
        }

        if (PreviewPathSupport.IsMediaPreviewType(preview.FileType)
            && (!PreviewCapabilities.IsVideo(preview) || PreviewCapabilities.ShouldPreviewVideo(settings, preview))
            && TryRenderMediaPreview(path, preview.FileType, token, cancellationToken))
        {
            return;
        }

        if (!IsCurrent(path, token, cancellationToken))
        {
            return;
        }

        ShowFileTypeIconPreview(row, preview);
    }

    private bool TryRenderTextPreview(
        string path,
        FilePreview preview,
        int token,
        CancellationToken cancellationToken)
    {
        if (preview.FileType != "text" || preview.Content is null)
        {
            return false;
        }

        if (!IsCurrent(path, token, cancellationToken))
        {
            return true;
        }

        ClearIcon();
        _textBox.Text = preview.Content;
        _textBox.Visibility = Visibility.Visible;
        _emptyText.Visibility = Visibility.Collapsed;
        return true;
    }

    private void ShowFileTypeIconPreview(FileRow row, FilePreview preview)
    {
        ShowIcon(row, FileTypePreviewLabel(row, preview));
        _emptyText.Text = IconPreviewMessage(preview);
        _emptyText.Visibility = Visibility.Visible;
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
}
