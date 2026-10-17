using System.Collections.Concurrent;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleFile.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace SimpleFile.App;

internal static class FileListThumbnailHost
{
    public const int MaxCachedImageThumbnails = 2048;
    public const long MaxCachedImageThumbnailBytes = 96L * 1024 * 1024;
    public const int MaxCachedVideoThumbnails = 512;
    public const long MaxCachedVideoThumbnailBytes = 32L * 1024 * 1024;
    private const int MaxInFlightThumbnails = 128;

    private static readonly BoundedLruCache<string, ImageSource> ImageCache = new(
        MaxCachedImageThumbnails, MaxCachedImageThumbnailBytes, StringComparer.OrdinalIgnoreCase);
    private static readonly BoundedLruCache<string, ImageSource> VideoCache = new(
        MaxCachedVideoThumbnails, MaxCachedVideoThumbnailBytes, StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, VideoThumbnailFrame> VideoFramePreferences = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim LoadGate = new(4, 4);
    private static readonly SemaphoreSlim VideoLoadGate = new(2, 2);

    private static Func<string, uint, CancellationToken, Task<byte[]>>? _loadImageThumbnailAsync;
    private static bool _primaryEnabled;
    private static bool _secondaryEnabled;

    public static event EventHandler? Changed;

    public static BoundedLruCacheStats ImageCacheStats => ImageCache.SnapshotStats();

    public static BoundedLruCacheStats VideoCacheStats => VideoCache.SnapshotStats();

    public static void Configure(Func<string, uint, CancellationToken, Task<byte[]>>? loadImageThumbnailAsync)
    {
        _loadImageThumbnailAsync = loadImageThumbnailAsync;
        if (loadImageThumbnailAsync is null)
        {
            _primaryEnabled = false;
            _secondaryEnabled = false;
        }

        ImageCache.Clear();
        VideoCache.Clear();
        InFlight.Clear();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static int InvalidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return 0;
        }

        var removed = ImageCache.RemoveWhere(key => ThumbnailCacheKey.MatchesPathOrSubtree(key, path))
            + VideoCache.RemoveWhere(key => ThumbnailCacheKey.MatchesPathOrSubtree(key, path));
        ShellIconLoader.InvalidatePath(path);
        return removed;
    }

    public static int InvalidatePaths(IEnumerable<string?> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var removed = 0;
        foreach (var path in paths)
        {
            removed += InvalidatePath(path);
        }

        return removed;
    }

    public static VideoThumbnailFrame VideoFrameForPath(string path) =>
        !string.IsNullOrWhiteSpace(path) && VideoFramePreferences.TryGetValue(path, out var frame)
            ? frame
            : VideoThumbnailFrame.Default;

    public static void SetVideoFramePreference(string path, VideoThumbnailFrame frame)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        VideoFramePreferences[path] = frame;
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
        return CanUseAppThumbnail(extension) || IsVideoThumbnail(extension) || CanUseWindowsThumbnail(extension);
    }

    public static ImageSource? CachedThumbnail(FileRow row, int iconSize)
    {
        if (!ShouldUseThumbnails(row, iconSize))
        {
            return null;
        }

        var extension = ExtensionFor(row);
        var cache = CacheForExtension(extension);
        return cache.TryGetValue(CacheKey(row, iconSize, extension), out var source)
            ? source
            : null;
    }

    public static async Task<ImageSource?> LoadThumbnailAsync(FileRow row, int iconSize, CancellationToken cancellationToken)
    {
        if (!ShouldUseThumbnails(row, iconSize))
        {
            return null;
        }

        var extension = ExtensionFor(row);
        var cache = CacheForExtension(extension);
        var key = CacheKey(row, iconSize, extension);
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var path = row.Path;
        var requestSize = ThumbnailSizeFor(iconSize);
        var isVideo = IsVideoThumbnail(extension);
        var videoFrame = isVideo
            ? VideoFrameForPath(path)
            : VideoThumbnailFrame.Default;
        if (InFlight.Count >= MaxInFlightThumbnails && !InFlight.ContainsKey(key))
        {
            return null;
        }

        if (isVideo || ForegroundTaskScheduler.Shared.IsForegroundActive)
        {
            await ForegroundTaskScheduler.Shared.YieldOrThrottleAsync(cancellationToken).ConfigureAwait(false);
            if (cache.TryGetValue(key, out cached))
            {
                return cached;
            }
        }

        var task = InFlight.GetOrAdd(
            key,
            _ => LoadAndCacheThumbnailAsync(cache, key, path, extension, requestSize, videoFrame, isVideo));
        return await task.WaitAsync(cancellationToken);
    }

    private static BoundedLruCache<string, ImageSource> CacheForExtension(string extension) =>
        IsVideoThumbnail(extension) ? VideoCache : ImageCache;

    private static async Task<ImageSource?> LoadAndCacheThumbnailAsync(
        BoundedLruCache<string, ImageSource> cache,
        string key,
        string path,
        string extension,
        int requestSize,
        VideoThumbnailFrame videoFrame,
        bool isVideo)
    {
        try
        {
            var gate = isVideo ? VideoLoadGate : LoadGate;
            await gate.WaitAsync();
            try
            {
                if (isVideo && ForegroundTaskScheduler.Shared.IsForegroundActive)
                {
                    await ForegroundTaskScheduler.Shared.YieldOrThrottleAsync(CancellationToken.None);
                }

                if (cache.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                var (source, encodedBytes) = await LoadCoreAsync(path, extension, requestSize, videoFrame);
                if (source is not null)
                {
                    cache.Set(
                        key,
                        source,
                        ThumbnailCacheKey.EstimateDecodedBytes(requestSize, key, encodedBytes));
                }

                return source;
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            InFlight.TryRemove(key, out _);
        }
    }

    private static async Task<(ImageSource? Source, int EncodedBytes)> LoadCoreAsync(
        string path,
        string extension,
        int requestSize,
        VideoThumbnailFrame videoFrame)
    {
        if (IsVideoThumbnail(extension))
        {
            var source = await VideoThumbnailExtractor.LoadAsync(path, requestSize, videoFrame);
            if (source is not null)
            {
                return (source, 0);
            }
        }

        if (CanUseAppThumbnail(extension) && _loadImageThumbnailAsync is not null)
        {
            try
            {
                var bytes = await _loadImageThumbnailAsync(path, (uint)requestSize, CancellationToken.None);
                if (bytes is not null && bytes.Length > 0)
                {
                    var source = await PreviewImageSourceFactory.FromBytesAsync(bytes, path, requestSize);
                    return (source, bytes.Length);
                }
            }
            catch
            {
                // Windows thumbnail providers get the next chance before falling back to the file icon.
            }
        }

        if (CanUseWindowsThumbnail(extension))
        {
            var source = await LoadWindowsThumbnailAsync(path, requestSize);
            return (source, 0);
        }

        return (null, 0);
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

            var bitmap = new BitmapImage
            {
                DecodePixelWidth = requestSize,
                DecodePixelType = DecodePixelType.Physical,
            };
            await bitmap.SetSourceAsync(thumbnail);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string CacheKey(FileRow row, int iconSize, string? resolvedExtension = null)
    {
        var size = ThumbnailSizeFor(iconSize);
        var extension = resolvedExtension ?? ExtensionFor(row);
        var videoFrame = IsVideoThumbnail(extension)
            ? VideoFrameForPath(row.Path).CacheToken
            : null;
        return ThumbnailCacheKey.Create(row.Path, row.Size, row.ModifiedText, size, videoFrame);
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

    private static bool IsVideoThumbnail(string extension) =>
        VideoThumbnailExtractor.CanUseVideoThumbnail(extension);

    private static bool CanUseWindowsThumbnail(string extension) =>
        PhotoFolder.IsImage(extension)
            || extension is "pdf"
            || MediaFolder.IsVideo(extension)
            || extension is "doc" or "docx" or "rtf" or "odt"
            or "xls" or "xlsx" or "xlsm" or "ods"
            or "ppt" or "pptx" or "pptm" or "odp"
            or "psd" or "ai" or "eps";
}

