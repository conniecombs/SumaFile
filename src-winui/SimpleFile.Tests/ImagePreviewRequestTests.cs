using System.Collections.Concurrent;
using System.Diagnostics;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;
using static SimpleFile.Tests.UiAuditSprint3Tests;

namespace SimpleFile.Tests;

/// <summary>
/// How the preview pane and Quick Look ask the file service for the picture of a format its media process decodes,
/// and what they show when it has none.
/// </summary>
public class ImagePreviewRequestTests
{
    private const string Photoshop = @"C:\art\cover.psd";
    private static readonly TimeSpan NeverInATest = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(20);

    [Fact]
    public void ASelectionRestsFor150MillisecondsBeforeItsPictureIsAskedFor()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(150), ThumbnailRouting.PreviewRequestDelay);
    }

    [Fact]
    public async Task APictureIsAskedForOnlyAfterTheSelectionHasRested()
    {
        var ipc = new PictureIpc();
        var service = new FileOperationService(ipc);
        var selected = Stopwatch.GetTimestamp();

        var picture = await service.RenderImagePreviewDebouncedAsync(Photoshop, CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2, 3 }, picture.Data);
        Assert.Equal([(Photoshop, 2560u)], ipc.PreviewRequests);
        AssertAskedAfterTheDelay(selected, ipc);
    }

    /// <summary>
    /// The service finishes every picture it is asked for, two at a time, and a cancel never reaches it. Arrowing
    /// through a folder of large files must therefore not ask for the ones passed on the way: the file the
    /// selection stops on would wait behind them.
    /// </summary>
    [Fact]
    public async Task AFileTheSelectionPassedOverIsNeverAskedFor()
    {
        var ipc = new PictureIpc();
        var service = new FileOperationService(ipc);
        using var selection = new CancellationTokenSource();

        var picture = service.RenderImagePreviewDebouncedAsync(Photoshop, NeverInATest, selection.Token);
        selection.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => picture.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(ipc.PreviewRequests);
    }

    /// <summary>Quick Look steps through a folder too, so its picture waits as the preview pane's does.</summary>
    [Fact]
    public async Task QuickLookShowsAMediaFormatAtScreenSize()
    {
        var ipc = new PictureIpc();
        var service = new FileOperationService(ipc);
        var selected = Stopwatch.GetTimestamp();

        var picture = await service.RenderQuickLookImageAsync(Photoshop, CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2, 3 }, picture);
        Assert.Equal([(Photoshop, 2560u)], ipc.PreviewRequests);
        Assert.Empty(ipc.ThumbnailRequests);
        AssertAskedAfterTheDelay(selected, ipc);
    }

    /// <summary>
    /// No media process, a damaged file, or one too large to decode: Quick Look shows the thumbnail, as before.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QuickLookFallsBackToTheThumbnailWhenTheMediaProcessHasNoPicture(bool serviceFails)
    {
        var ipc = new PictureIpc { Preview = serviceFails ? null : [] };
        var service = new FileOperationService(ipc);

        var picture = await service.RenderQuickLookImageAsync(Photoshop, Brief, CancellationToken.None);

        Assert.Equal(new byte[] { 9 }, picture);
        Assert.Equal([(Photoshop, 2560u)], ipc.PreviewRequests);
        Assert.Equal([(Photoshop, 512u)], ipc.ThumbnailRequests);
    }

    /// <summary>
    /// The media process makes no preview of these (of an SVG it makes only the thumbnail), so nothing changes
    /// for them.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Photos\IMG_0001.jpg")]
    [InlineData(@"C:\Photos\IMG_0001.heic")]
    [InlineData(@"C:\Icons\logo.svg")]
    public async Task QuickLookKeepsTheThumbnailOfEveryOtherPicture(string path)
    {
        var ipc = new PictureIpc();
        var service = new FileOperationService(ipc);

        // Nothing waits here: with a wait, this call would not finish within the test.
        var picture = await service.RenderQuickLookImageAsync(path, NeverInATest, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new byte[] { 9 }, picture);
        Assert.Empty(ipc.PreviewRequests);
        Assert.Equal([(path, 512u)], ipc.ThumbnailRequests);
    }

    /// <summary>A file Left or Right stepped past in Quick Look asks for nothing, not even its thumbnail.</summary>
    [Fact]
    public async Task AFileQuickLookSteppedPastIsNeverAskedFor()
    {
        var ipc = new PictureIpc();
        var service = new FileOperationService(ipc);
        using var step = new CancellationTokenSource();

        var picture = service.RenderQuickLookImageAsync(Photoshop, NeverInATest, step.Token);
        step.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => picture.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(ipc.PreviewRequests);
        Assert.Empty(ipc.ThumbnailRequests);
    }

    [Fact]
    public void ThePreviewPaneWaitsBeforeItAsksTheService()
    {
        var image = Read("PreviewPresenter.Image.cs");
        var service = Slice(image, "private async Task<bool> TrySetServiceImageAsync(", "private async Task<bool> TrySetAutomaticImageAsync(");

        Assert.Contains("await fileOps.RenderImagePreviewDebouncedAsync(path, cancellationToken);", service);
        Assert.DoesNotContain("fileOps.RenderImagePreviewAsync(", service);
    }

    [Fact]
    public void QuickLookAsksForTheScreenSizedPicture()
    {
        var inspection = Read("MainWindow.InspectionDialogs.cs");
        var quickLookImage = Slice(inspection, "private static async Task<bool> TryAddQuickLookImageAsync(", "private async Task ShowPropertiesAsync()");

        Assert.Contains("imageData = await fileOps.RenderQuickLookImageAsync(row.Path, cancellationToken);", quickLookImage);
        Assert.DoesNotContain("GenerateThumbnailAsync(", quickLookImage);
    }

    /// <summary>
    /// A <c>BitmapImage</c> given a path never says that Windows has no codec for the file: the pane hid its icon
    /// card and stayed blank. For a format the media process decodes, Windows' own load is therefore awaited, and
    /// a failure leaves the icon card to the caller. Every other picture loads from its path, as before.
    /// </summary>
    [Fact]
    public void AMediaFormatWindowsCannotLoadFallsBackToTheIconCard()
    {
        var image = Read("PreviewPresenter.Image.cs");
        var automatic = Slice(image, "private async Task<bool> TrySetAutomaticImageAsync(", "private async Task<bool> TrySetWindowsDecodedImageAsync(");
        var decoded = Slice(image, "private async Task<bool> TrySetWindowsDecodedImageAsync(", "private async Task<bool> TrySetWindowsImageThumbnailAsync(");

        var checkedLoad = automatic.IndexOf("if (ThumbnailRouting.PreviewsThroughService(path))", StringComparison.Ordinal);
        var pathLoad = automatic.IndexOf("new BitmapImage { UriSource = new Uri(path) }", StringComparison.Ordinal);
        Assert.True(checkedLoad >= 0, "A media format is loaded with the check.");
        Assert.True(pathLoad > checkedLoad, "Every other picture still loads from its path, as before.");
        Assert.Contains("return await TrySetWindowsDecodedImageAsync(path, token, cancellationToken);", automatic[checkedLoad..pathLoad]);

        Assert.Contains("var decoder = await BitmapDecoder.CreateAsync(stream);", decoded);
        // What stays in memory is capped to the pane's size before the load.
        var cap = decoded.IndexOf("ThumbnailRouting.PreviewDecodeSize(decoder.PixelWidth, decoder.PixelHeight)", StringComparison.Ordinal);
        Assert.True(cap >= 0 && cap < decoded.IndexOf("await source.SetSourceAsync(stream);", StringComparison.Ordinal));
        Assert.Contains("await source.SetSourceAsync(stream);", decoded);
        Assert.DoesNotContain("UriSource", decoded);
        // Nothing is shown until the load has succeeded, and a failure shows nothing.
        Assert.True(
            decoded.IndexOf("await source.SetSourceAsync(stream);", StringComparison.Ordinal)
                < decoded.IndexOf("_image.Source = source;", StringComparison.Ordinal));
        Assert.Contains("return false;", decoded[decoded.IndexOf("catch", StringComparison.Ordinal)..]);
    }

    /// <summary>
    /// A wait that ends late proves nothing wrong, and a timer may end one early by its resolution (about 16 ms on
    /// Windows), so the request must come no sooner than the delay less twice that.
    /// </summary>
    private static void AssertAskedAfterTheDelay(long selected, PictureIpc ipc)
    {
        var waited = Stopwatch.GetElapsedTime(selected, ipc.PreviewRequestedAt);
        var least = ThumbnailRouting.PreviewRequestDelay - TimeSpan.FromMilliseconds(32);
        Assert.True(waited >= least, $"The picture was asked for after {waited.TotalMilliseconds:0} ms.");
    }

    private sealed class PictureIpc : NullIpc
    {
        /// <summary>What the media process answers; null when it has no picture and the request fails.</summary>
        public byte[]? Preview { get; init; } = [1, 2, 3];

        public ConcurrentQueue<(string Path, uint MaxDimension)> PreviewRequests { get; } = new();

        public ConcurrentQueue<(string Path, uint Size)> ThumbnailRequests { get; } = new();

        /// <summary>The <see cref="Stopwatch"/> timestamp of the latest preview request.</summary>
        public long PreviewRequestedAt { get; private set; }

        public override Task<EncodedImage> RenderImagePreviewAsync(string path, uint maxDimension, CancellationToken ct = default)
        {
            PreviewRequestedAt = Stopwatch.GetTimestamp();
            PreviewRequests.Enqueue((path, maxDimension));
            return Preview is null
                ? Task.FromException<EncodedImage>(new IpcException(Protocol.ErrApplication, "Unsupported image format: psd"))
                : Task.FromResult(new EncodedImage { Format = "jpeg", Width = 4, Height = 3, Data = Preview });
        }

        public override Task<byte[]> GenerateThumbnailAsync(string path, uint size, CancellationToken ct = default)
        {
            ThumbnailRequests.Enqueue((path, size));
            return Task.FromResult<byte[]>([9]);
        }
    }
}
