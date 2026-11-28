namespace SimpleFile.Core;

/// <summary>
/// Which pictures SumaFile decodes itself, and through which part of it: the file service decodes six common
/// formats in its own process, and its media process (<c>sumafile-media</c>) decodes the formats in
/// <see cref="MediaFormats"/>. Everything else is left to Windows' own thumbnails and codecs.
/// </summary>
public static class ThumbnailRouting
{
    /// <summary>The long side, in pixels, of the picture the preview pane asks the file service for.</summary>
    public const uint PreviewMaxDimension = 2560;

    /// <summary>
    /// How long a selection rests on a file before its picture is asked for. The file service finishes every
    /// picture it is asked for, two at a time, so the files passed while arrowing through a folder must not be
    /// asked for: the file the selection stops on would wait behind them.
    /// </summary>
    public static readonly TimeSpan PreviewRequestDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The decode size to give Windows for a picture of this many pixels, so that what stays in memory is no larger
    /// than the preview pane shows. Only the long side is set, to <see cref="PreviewMaxDimension"/>; the zero keeps
    /// the picture's shape. Both are zero when the picture already fits.
    /// </summary>
    public static (int Width, int Height) PreviewDecodeSize(uint pixelWidth, uint pixelHeight)
    {
        if (Math.Max(pixelWidth, pixelHeight) <= PreviewMaxDimension)
        {
            return (0, 0);
        }

        return pixelWidth >= pixelHeight ? ((int)PreviewMaxDimension, 0) : (0, (int)PreviewMaxDimension);
    }

    /// <summary>
    /// True when the file service makes thumbnails for <paramref name="extension"/> (lowercase, without a dot).
    /// Windows' thumbnail is still tried when the service fails.
    /// </summary>
    public static bool CanUseAppThumbnail(string extension) =>
        extension is "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp" || UsesMediaProcess(extension);

    /// <summary>
    /// True when a thumbnail of <paramref name="extension"/> comes from the media process. Such thumbnails load
    /// through a gate of their own, so a slow file there never takes a slot the built-in formats use.
    /// </summary>
    public static bool UsesMediaProcess(string extension) =>
        MediaFormats.IsMediaProcessFormat(extension);

    /// <summary>
    /// True when Windows' own thumbnail is worth asking for: pictures, videos, common documents, and every format
    /// the media process decodes. For the last group it is the fallback when SumaFile's decoder has no picture.
    /// </summary>
    public static bool CanUseWindowsThumbnail(string extension) =>
        PhotoFolder.IsImage(extension)
            || extension is "pdf"
            || MediaFolder.IsVideo(extension)
            || extension is "doc" or "docx" or "rtf" or "odt"
            or "xls" or "xlsx" or "xlsm" or "ods"
            or "ppt" or "pptx" or "pptm" or "odp"
            or "ai" or "eps"
            || MediaFormats.IsMediaProcessFormat(extension);

    /// <summary>
    /// True when the preview pane asks the file service for the picture first. That is every media-process
    /// format except SVG, which WinUI draws as vectors.
    /// </summary>
    public static bool PreviewsThroughService(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension.Length > 0 && extension != "svg" && MediaFormats.IsMediaProcessFormat(extension);
    }

    /// <summary>
    /// True when <paramref name="bytes"/> start as a JPEG, PNG, GIF, BMP or WebP file. The file service answers
    /// with one of these whatever the file is, so the bytes say how to read them, not the file's name: the
    /// thumbnail of an SVG is a JPEG.
    /// </summary>
    public static bool IsEncodedPicture(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith<byte>([0xFF, 0xD8, 0xFF])
        || bytes.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
        || bytes.StartsWith("GIF8"u8)
        || bytes.StartsWith("BM"u8)
        || (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8));
}
