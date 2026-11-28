using Xunit;
using static SimpleFile.Tests.UiAuditSprint3Tests;

namespace SimpleFile.Tests;

/// <summary>
/// How the app asks for video thumbnails. The code is WinUI code that cannot run in a test, so these read it:
/// video thumbnails come from the file service, network videos queue apart from local ones, a queued one nobody
/// wants is dropped, and the preview pane's frame picture comes from the same request.
/// </summary>
public class VideoThumbnailRequestTests
{
    private static string Host() => Read("FileListThumbnailHost.cs");

    [Fact]
    public void AListThumbnailOfAVideoIsAskedOfTheFileService()
    {
        var load = Slice(Host(), "private static async Task<(ImageSource? Source, int EncodedBytes)> LoadCoreAsync(", "private static async Task<ImageSource?> LoadWindowsThumbnailAsync(");

        Assert.Contains("await _loadVideoThumbnailAsync(path, (uint)requestSize, videoFrame.Percent, CancellationToken.None)", load);
        Assert.DoesNotContain("VideoThumbnailExtractor.LoadAsync", load);
        // The service's frame first, then Windows' own thumbnail.
        Assert.True(
            load.IndexOf("_loadVideoThumbnailAsync(", StringComparison.Ordinal)
                < load.IndexOf("LoadWindowsThumbnailAsync(path, requestSize)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFileServiceIsWiredInForVideos()
    {
        var host = Host();
        var chrome = Read("MainWindow.Chrome.cs");

        Assert.Contains("Func<string, uint, int, CancellationToken, Task<byte[]>>? loadVideoThumbnailAsync", host);
        Assert.Contains("FileListThumbnailHost.Configure(LoadFileListImageThumbnailAsync, LoadFileListVideoThumbnailAsync);", Read("MainWindow.xaml.cs"));
        Assert.Contains("return fileOps.GenerateVideoThumbnailAsync(path, size, positionPercent, cancellationToken);", chrome);
    }

    /// <summary>A slow share must never take one of the two slots local videos load through.</summary>
    [Fact]
    public void VideosOnANetworkDriveQueueApart()
    {
        var host = Host();

        Assert.Contains("private static readonly SemaphoreSlim NetworkVideoLoadGate = new(1, 1);", host);
        Assert.Contains("private static readonly SemaphoreSlim VideoLoadGate = new(2, 2);", host);
        Assert.Contains("NetworkPaths.Shared.IsNetwork(path) ? NetworkVideoLoadGate : VideoLoadGate", host);
    }

    [Fact]
    public void AQueuedVideoThumbnailNobodyWantsIsDropped()
    {
        var host = Host();
        var request = Slice(host, "public static async Task<ImageSource?> LoadThumbnailAsync(", "private static BoundedLruCache<string, ImageSource> CacheForExtension(");
        var queued = Slice(host, "private static async Task<ImageSource?> LoadAndCacheThumbnailAsync(", "private static async Task<(ImageSource? Source, int EncodedBytes)> LoadCoreAsync(");

        // The caller is counted before the load can reach the front of its queue.
        var want = request.IndexOf("Wanted.Want(key)", StringComparison.Ordinal);
        Assert.True(want >= 0 && want < request.IndexOf("InFlight.GetOrAdd(", StringComparison.Ordinal));
        // At the front of the queue, before anything is asked of the service.
        var gate = queued.IndexOf("await gate.WaitAsync();", StringComparison.Ordinal);
        var check = queued.IndexOf("if (isVideo && !Wanted.IsWanted(key))", StringComparison.Ordinal);
        var load = queued.IndexOf("await LoadCoreAsync(", StringComparison.Ordinal);
        Assert.True(gate >= 0 && gate < check && check < load);
    }

    [Fact]
    public void ThePreviewPanesFramePictureComesFromTheFileService()
    {
        var video = Read("PreviewPresenter.Video.cs");
        var frame = Slice(video, "private async Task LoadVideoFramePreviewAsync(", "private bool IsCurrentVideoFrame(");

        Assert.DoesNotContain("VideoThumbnailExtractor", frame);
        // After the selection has rested, and never more than one with the service: it finishes every request
        // it is sent, and the picture must not take the slots the list's thumbnails wait for.
        Assert.Contains("_videoFramePicture.RunAsync(", frame);
        Assert.Contains("fileOps.GenerateVideoFramePictureAsync(path, 144, frame.Percent, cancellationToken)", frame);
        Assert.DoesNotContain("GenerateVideoThumbnailAsync(", frame);
    }
}
