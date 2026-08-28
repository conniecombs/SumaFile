using System.Collections.Concurrent;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleFile.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace SimpleFile.App;

internal static class FileListThumbnailHost
{
    private const int MaxCachedThumbnails = 512;

    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim LoadGate = new(4, 4);

    private static Func<string, uint, CancellationToken, Task<string>>? _loadImageThumbnailAsync;
    private static bool _primaryEnabled;
    private static bool _secondaryEnabled;

    public static event EventHandler? Changed;

    public static void Configure(Func<string, uint, CancellationToken, Task<string>>? loadImageThumbnailAsync)
    {
        _loadImageThumbnailAsync = loadImageThumbnailAsync;
        if (loadImageThumbnailAsync is null)
        {
            _primaryEnabled = false;
            _secondaryEnabled = false;
        }

        Cache.Clear();
        InFlight.Clear();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ApplyPolicy(PaneId pane, bool enabled)
    {
        var changed = pane == PaneId.Secondary
            ? _secondaryEnabled != enabled
            : _primaryEnabled != enabled;
        if (!changed)
        {
            return;
        }

        if (pane == PaneId.Secondary)
        {
            _secondaryEnabled = enabled;
        }
        else
        {
            _primaryEnabled = enabled;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static bool ShouldUseThumbnails(FileRow row, int iconSize)
    {
        if (row.IsDir || iconSize <= 0 || string.IsNullOrWhiteSpace(row.Path))
        {
            return false;
        }

        var paneEnabled = row.Pane == PaneId.Secondary ? _secondaryEnabled : _primaryEnabled;
        if (!paneEnabled)
        {
            return false;
        }

        var extension = ExtensionFor(row);
        return CanUseAppThumbnail(extension) || CanUseWindowsThumbnail(extension);
    }

    public static ImageSource? CachedThumbnail(FileRow row, int iconSize)
    {
        return ShouldUseThumbnails(row, iconSize)
            && Cache.TryGetValue(CacheKey(row, iconSize), out var source)
                ? source
                : null;
    }

    public static async Task<ImageSource?> LoadThumbnailAsync(FileRow row, int iconSize, CancellationToken cancellationToken)
    {
        if (!ShouldUseThumbnails(row, iconSize))
        {
            return null;
        }

        var key = CacheKey(row, iconSize);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var path = row.Path;
        var extension = ExtensionFor(row);
        var requestSize = ThumbnailSizeFor(iconSize);
        var task = InFlight.GetOrAdd(
            key,
            _ => LoadAndCacheThumbnailAsync(key, path, extension, requestSize));
        return await task.WaitAsync(cancellationToken);
    }

    private static async Task<ImageSource?> LoadAndCacheThumbnailAsync(
        string key,
        string path,
        string extension,
        int requestSize)
    {
        try
        {
            await LoadGate.WaitAsync();
            try
            {
                var source = await LoadCoreAsync(path, extension, requestSize);
                if (source is not null)
                {
                    if (Cache.Count > MaxCachedThumbnails)
                    {
                        Cache.Clear();
                    }

                    Cache[key] = source;
                }

                return source;
            }
            finally
            {
                LoadGate.Release();
            }
        }
        finally
        {
            InFlight.TryRemove(key, out _);
        }
    }

    private static async Task<ImageSource?> LoadCoreAsync(string path, string extension, int requestSize)
    {
        if (CanUseAppThumbnail(extension) && _loadImageThumbnailAsync is not null)
        {
            try
            {
                var base64 = await _loadImageThumbnailAsync(path, (uint)requestSize, CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(base64))
                {
                    return await PreviewImageSourceFactory.FromBase64Async(base64, path);
                }
            }
            catch
            {
                // Windows thumbnail providers get the next chance before falling back to the file icon.
            }
        }

        return CanUseWindowsThumbnail(extension)
            ? await LoadWindowsThumbnailAsync(path, requestSize)
            : null;
    }

    private static async Task<ImageSource?> LoadWindowsThumbnailAsync(string path, int requestSize)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem,
                (uint)requestSize,
                ThumbnailOptions.UseCurrentScale);
            if (thumbnail.Size == 0 || thumbnail.Type == ThumbnailType.Icon)
            {
                return null;
            }

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string CacheKey(FileRow row, int iconSize)
    {
        var size = ThumbnailSizeFor(iconSize);
        return $"{size}|{row.Path}|{row.Size}|{row.ModifiedText}";
    }

    private static int ThumbnailSizeFor(int iconSize) =>
        Math.Clamp(iconSize <= 32 ? 64 : iconSize * 2, 64, UiSettings.IconSizeMax * 2);

    private static string ExtensionFor(FileRow row)
    {
        var extension = string.IsNullOrWhiteSpace(row.Extension)
            ? System.IO.Path.GetExtension(row.Path)
            : row.Extension;
        return extension.Trim().TrimStart('.').ToLowerInvariant();
    }

    private static bool CanUseAppThumbnail(string extension) =>
        extension is "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp";

    private static bool CanUseWindowsThumbnail(string extension) =>
        extension is
            "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp" or "tif" or "tiff"
            or "svg" or "ico" or "cur" or "heic" or "heif" or "avif" or "jxl"
            or "pdf"
            or "mp4" or "m4v" or "mov" or "webm" or "mkv" or "avi" or "wmv" or "mpg" or "mpeg"
            or "doc" or "docx" or "rtf" or "odt"
            or "xls" or "xlsx" or "xlsm" or "ods"
            or "ppt" or "pptx" or "pptm" or "odp"
            or "psd" or "ai" or "eps";
}
