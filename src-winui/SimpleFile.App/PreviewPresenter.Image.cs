using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace SimpleFile.App;

internal sealed partial class PreviewPresenter
{
    private async Task<bool> TryRenderImagePreviewAsync(
        FileRow row,
        FilePreview preview,
        int token,
        CancellationToken cancellationToken)
    {
        var path = row.Path;
        if (preview.FileType != "image")
        {
            return false;
        }

        if (preview.Content is null && await TrySetAutomaticImageAsync(path, token, cancellationToken))
        {
            ClearIcon();
            _emptyText.Visibility = Visibility.Collapsed;
            return true;
        }

        if (preview.Content is not null && await TrySetImageAsync(preview.Content, path, token, cancellationToken))
        {
            ClearIcon();
            _emptyText.Visibility = Visibility.Collapsed;
            return true;
        }

        try
        {
            var thumbnail = await _workspace()!.FileOps!.GenerateThumbnailAsync(path, 256, cancellationToken);
            if (await TrySetImageAsync(thumbnail, path, token, cancellationToken))
            {
                ClearIcon();
                _emptyText.Text = "Thumbnail preview";
                return true;
            }
        }
        catch
        {
            // Unsupported image codecs still keep metadata and actions visible.
        }

        return false;
    }

    private async Task<bool> TrySetAutomaticImageAsync(string path, int token, CancellationToken cancellationToken)
    {
        if (!PreviewPathSupport.CanUsePathBackedPreview(path, "image") || !IsCurrent(path, token, cancellationToken))
        {
            return false;
        }

        var extension = PreviewCapabilities.ExtensionFor(path);
        if (await TrySetWindowsImageThumbnailAsync(path, token, cancellationToken))
        {
            return true;
        }

        try
        {
            ImageSource source = extension == "svg"
                ? new SvgImageSource(new Uri(path))
                : new BitmapImage { UriSource = new Uri(path) };
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

    private async Task<bool> TrySetWindowsImageThumbnailAsync(string path, int token, CancellationToken cancellationToken)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem,
                512,
                ThumbnailOptions.UseCurrentScale);
            if (thumbnail.Size == 0 || thumbnail.Type == ThumbnailType.Icon)
            {
                return false;
            }

            var source = new BitmapImage();
            await source.SetSourceAsync(thumbnail);
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
            return false;
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

    private async Task<bool> TrySetImageAsync(byte[] bytes, string path, int token, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsCurrent(path, token, cancellationToken))
            {
                return false;
            }

            var source = await PreviewImageSourceFactory.FromBytesAsync(bytes, path);

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
}
