using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Windows.Graphics.Imaging;
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

        // Formats the file service's media process decodes: its picture first, whatever codecs Windows has.
        // When it has none for this file, the paths below take over, as before.
        if (ThumbnailRouting.PreviewsThroughService(path)
            && await TrySetServiceImageAsync(path, token, cancellationToken))
        {
            ClearIcon();
            _emptyText.Visibility = Visibility.Collapsed;
            return true;
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

    private async Task<bool> TrySetServiceImageAsync(string path, int token, CancellationToken cancellationToken)
    {
        try
        {
            var fileOps = _workspace()?.FileOps;
            if (fileOps is null || !IsCurrent(path, token, cancellationToken))
            {
                return false;
            }

            // Sent only once the selection has rested here: the service finishes every picture it is asked for.
            var picture = await fileOps.RenderImagePreviewDebouncedAsync(path, cancellationToken);
            if (picture.Data.Length == 0 || !IsCurrent(path, token, cancellationToken))
            {
                return false;
            }

            var source = await PreviewImageSourceFactory.FromBytesAsync(picture.Data, path);
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
            // The media process has no picture for this file: not installed, too large, damaged, or timed out.
            // Or the selection moved on before the picture was asked for.
            return false;
        }
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

        // Windows often has no codec for a format the media process decodes, and a BitmapImage given only the path
        // never says so: the pane would hide its icon card and stay blank. These are loaded where a failure shows.
        if (ThumbnailRouting.PreviewsThroughService(path))
        {
            return await TrySetWindowsDecodedImageAsync(path, token, cancellationToken);
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

    /// <summary>
    /// Shows <paramref name="path"/> decoded by Windows' codecs, and says whether Windows could decode it. When it
    /// could not, nothing is shown here and the pane ends on its icon card.
    /// </summary>
    private async Task<bool> TrySetWindowsDecodedImageAsync(string path, int token, CancellationToken cancellationToken)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            // Windows finds a codec from the file's first bytes, or fails here. Asked before the load below, which
            // is handed the whole file: these files can be gigabytes.
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (!IsCurrent(path, token, cancellationToken))
            {
                return false;
            }

            stream.Seek(0);
            // Keep no more pixels than the pane shows: these files can be hundreds of megapixels.
            var (decodeWidth, decodeHeight) = ThumbnailRouting.PreviewDecodeSize(decoder.PixelWidth, decoder.PixelHeight);
            var source = new BitmapImage { DecodePixelWidth = decodeWidth, DecodePixelHeight = decodeHeight };
            await source.SetSourceAsync(stream);
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
            // Windows has no codec for this file, or could not decode it.
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
