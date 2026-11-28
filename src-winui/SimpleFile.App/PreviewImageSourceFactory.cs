using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace SimpleFile.App;

internal static class PreviewImageSourceFactory
{
    public static async Task<ImageSource> FromBase64Async(string base64, string path, int decodePixelSize = 0)
    {
        var bytes = Convert.FromBase64String(base64);
        return await FromBytesAsync(bytes, path, decodePixelSize);
    }

    public static async Task<ImageSource> FromBytesAsync(byte[] bytes, string path, int decodePixelSize = 0)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        // An SVG file's own text is drawn as vectors. Its thumbnail from the file service is a JPEG, though:
        // what the bytes are decides, not the file's name.
        if (System.IO.Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase)
            && !SimpleFile.Core.ThumbnailRouting.IsEncodedPicture(bytes))
        {
            var svg = new SvgImageSource();
            if (decodePixelSize > 0)
            {
                svg.RasterizePixelWidth = decodePixelSize;
                svg.RasterizePixelHeight = decodePixelSize;
            }

            await svg.SetSourceAsync(stream);
            return svg;
        }

        var bitmap = new BitmapImage();
        if (decodePixelSize > 0)
        {
            bitmap.DecodePixelWidth = decodePixelSize;
            bitmap.DecodePixelType = DecodePixelType.Physical;
        }

        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}

