#if DEBUG
using System.Diagnostics;
using SimpleFile.Core;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// Video thumbnails of a folder of generated videos (see write-test-avi in crates/sumafile-video). They are
    /// small and uncompressed, so nothing depends on the codecs this machine has, and no machine has a hardware
    /// decoder for them.
    /// <para>
    /// Three passes over the same files, one file at a time: the new path (the file service's video process,
    /// nothing cached), the old path (the in-app extractor), and the new path again (from the cache). Every file
    /// must get a thumbnail on the new path. The old path is timed for comparison: the in-app extractor is
    /// removed only if the new path is not slower on a machine without a hardware decoder, which is what the
    /// test machine is.
    /// </para>
    /// </summary>
    private async Task SmokeVideoThumbnailsAsync(ExplorerWorkspace workspace, string root, string videos, List<object> results)
    {
        var fileOps = workspace.FileOps ?? throw new InvalidOperationException("File operations are not available.");
        var cacheFolder = Path.Combine(root, "state", "video-thumbnail-cache");
        Directory.CreateDirectory(cacheFolder);
        var previousCache = workspace.Settings.ThumbnailCachePath;
        workspace.Settings.ThumbnailCachePath = cacheFolder;
        await workspace.SaveUiSettingsAsync();
        try
        {
            const uint size = 192;
            var files = Directory.GetFiles(videos, "*.avi").Order(StringComparer.OrdinalIgnoreCase).ToArray();
            SmokeAssert(files.Length > 0, "The video fixture is empty");
            var before = await fileOps.GetMediaStatusAsync(CancellationToken.None);

            // New: asked of the file service, decoded by its video process.
            var timer = Stopwatch.StartNew();
            foreach (var file in files)
            {
                var bytes = await fileOps.GenerateVideoThumbnailAsync(file, size, VideoThumbnailFrame.DefaultPercent, CancellationToken.None);
                var source = bytes.Length > 0 ? await PreviewImageSourceFactory.FromBytesAsync(bytes, file, (int)size) : null;
                SmokeAssert(source is not null, $"No video thumbnail for {Path.GetFileName(file)}");
            }
            var newMs = timer.Elapsed.TotalMilliseconds;

            // Old: the in-app extractor, the same files in the same order.
            timer.Restart();
            var oldPictures = 0;
            foreach (var file in files)
            {
                if (await VideoThumbnailExtractor.LoadAsync(file, (int)size, VideoThumbnailFrame.Default) is not null)
                {
                    oldPictures++;
                }
            }
            var oldMs = timer.Elapsed.TotalMilliseconds;

            // Warm: the same requests again. Nothing is decoded this time.
            timer.Restart();
            foreach (var file in files)
            {
                var bytes = await fileOps.GenerateVideoThumbnailAsync(file, size, VideoThumbnailFrame.DefaultPercent, CancellationToken.None);
                SmokeAssert(bytes.Length > 0, $"No cached video thumbnail for {Path.GetFileName(file)}");
            }
            var warmMs = timer.Elapsed.TotalMilliseconds;

            var after = await fileOps.GetMediaStatusAsync(CancellationToken.None);
            var decoded = after.VideoHardware + after.VideoSoftware - before.VideoHardware - before.VideoSoftware;
            SmokeAssert(decoded == (ulong)files.Length,
                $"{decoded} video thumbnails were decoded for {files.Length} videos: each is decoded once and then comes from the cache");
            SmokeAssert(after.VideoHardware == before.VideoHardware,
                "An uncompressed video has no decoder, so the graphics card cannot have decoded one");

            results.Add(new
            {
                scenario = "video-thumbnails", videos = files.Length,
                newMs, newPerThumbnailMs = newMs / files.Length,
                oldMs, oldPictures, oldPerThumbnailMs = oldMs / files.Length,
                warmMs, warmPerThumbnailMs = warmMs / files.Length,
                videoHardware = after.VideoHardware, videoSoftware = after.VideoSoftware,
                lastSoftwareReason = after.LastSoftwareReason, passed = true,
            });
        }
        finally
        {
            workspace.Settings.ThumbnailCachePath = previousCache;
            await workspace.SaveUiSettingsAsync();
        }
    }
}
#endif
