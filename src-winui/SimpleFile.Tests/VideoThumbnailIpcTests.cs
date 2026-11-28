using System.Text.Json;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Video thumbnails over IPC: <c>generate_thumbnail</c> with the position of the frame, and
/// <c>get_media_status</c>, which says who decoded them.
/// </summary>
public class VideoThumbnailIpcTests
{
    [Fact]
    public async Task AVideoThumbnailRequestCarriesItsPosition()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var call = client.GenerateVideoThumbnailAsync(@"C:\films\clip.mp4", 192, 40);
        var request = await server.ReadRequestAsync();
        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeThumbnailResult(request.Id, [1, 2, 3]));
        var bytes = await call;

        Assert.Equal("generate_thumbnail", request.Method);
        var parameters = Assert.IsType<JsonElement>(request.Params);
        Assert.Equal(@"C:\films\clip.mp4", parameters.GetProperty("path").GetString());
        Assert.Equal(192u, parameters.GetProperty("size").GetUInt32());
        Assert.Equal(40, parameters.GetProperty("positionPercent").GetInt32());
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    /// <summary>A picture has no frame to choose, and its request stays as it was.</summary>
    [Fact]
    public async Task APictureThumbnailRequestCarriesNone()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var call = client.GenerateThumbnailAsync(@"C:\img\a.jpg", 128);
        var request = await server.ReadRequestAsync();
        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeThumbnailResult(request.Id, [9]));
        await call;

        var parameters = Assert.IsType<JsonElement>(request.Params);
        Assert.False(parameters.TryGetProperty("positionPercent", out _));
    }

    [Fact]
    public async Task TheMediaStatusIsRead()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var call = client.GetMediaStatusAsync();
        var request = await server.ReadRequestAsync();
        await server.SendResultAsync(
            request.Id,
            new { videoHardware = 212, videoSoftware = 3, lastSoftwareReason = "no_hardware_decoder" });
        var status = await call;

        Assert.Equal("get_media_status", request.Method);
        Assert.Equal(212ul, status.VideoHardware);
        Assert.Equal(3ul, status.VideoSoftware);
        Assert.Equal("no_hardware_decoder", status.LastSoftwareReason);
    }

    [Fact]
    public async Task AStatusWithNoProcessorDecodeHasNoReason()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var call = client.GetMediaStatusAsync();
        var request = await server.ReadRequestAsync();
        await server.SendResultAsync(
            request.Id,
            new { videoHardware = 5, videoSoftware = 0, lastSoftwareReason = (string?)null });
        var status = await call;

        Assert.Equal(5ul, status.VideoHardware);
        Assert.Null(status.LastSoftwareReason);
    }

    /// <summary>The app's file service passes both requests through.</summary>
    [Fact]
    public async Task TheFileServicePassesBothThrough()
    {
        var ipc = new VideoIpc();
        var service = new FileOperationService(ipc);

        var bytes = await service.GenerateVideoThumbnailAsync(@"C:\films\clip.mp4", 144, 60, CancellationToken.None);
        var status = await service.GetMediaStatusAsync(CancellationToken.None);

        Assert.Equal(new byte[] { 7 }, bytes);
        Assert.Equal((@"C:\films\clip.mp4", 144u, 60), ipc.Asked);
        Assert.Equal(2ul, status.VideoSoftware);
    }

    private sealed class VideoIpc : NullIpc
    {
        public (string Path, uint Size, int PositionPercent) Asked { get; private set; }

        public override Task<byte[]> GenerateVideoThumbnailAsync(
            string path,
            uint size,
            int positionPercent,
            CancellationToken ct = default)
        {
            Asked = (path, size, positionPercent);
            return Task.FromResult<byte[]>([7]);
        }

        public override Task<MediaStatus> GetMediaStatusAsync(CancellationToken ct = default) =>
            Task.FromResult(new MediaStatus { VideoSoftware = 2 });
    }
}
