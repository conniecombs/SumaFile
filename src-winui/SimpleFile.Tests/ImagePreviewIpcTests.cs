using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// <c>render_image_preview</c>: a picture for the preview pane, decoded by the file service's media process.
/// </summary>
public class ImagePreviewIpcTests
{
    /// <summary>
    /// The frame the Rust service's encoder produces for this preview (its test
    /// <c>image_preview_frame_matches_the_golden_bytes</c> asserts the same bytes): request 7, a 640 by 480 PNG
    /// of five bytes.
    /// </summary>
    private const string GoldenImagePreview =
        "53464231030A0700000003000000706E6780020000E0010000050000000102030405";

    [Fact]
    public void BinaryPreview_DecodesTheServicesGoldenFrame()
    {
        var frame = Convert.FromHexString(GoldenImagePreview);

        Assert.True(BinaryFrameCodec.TryDecode(frame, out var message));
        Assert.Equal(7, message!.ResponseId);
        var preview = Assert.IsType<EncodedImage>(message.Payload);
        Assert.Equal(("png", 640u, 480u), (preview.Format, preview.Width, preview.Height));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, preview.Data);
        // And the host's own encoder writes the same bytes.
        Assert.Equal(GoldenImagePreview, Convert.ToHexString(BinaryFrameCodec.EncodeImagePreviewResult(7, preview)));
    }

    [Fact]
    public async Task RenderImagePreview_SendsThePathAndSizeAndTakesABinaryReply()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var call = client.RenderImagePreviewAsync(@"C:\art\cover.psd", 2560);
        var request = await server.ReadRequestAsync();
        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeImagePreviewResult(
            request.Id,
            new EncodedImage { Format = "jpeg", Width = 1200, Height = 800, Data = [9, 8, 7] }));
        var preview = await call;

        Assert.Equal("render_image_preview", request.Method);
        var parameters = Assert.IsType<System.Text.Json.JsonElement>(request.Params);
        Assert.Equal(@"C:\art\cover.psd", parameters.GetProperty("path").GetString());
        Assert.Equal(2560u, parameters.GetProperty("maxDimension").GetUInt32());
        Assert.Equal(("jpeg", 1200u, 800u), (preview.Format, preview.Width, preview.Height));
        Assert.Equal(new byte[] { 9, 8, 7 }, preview.Data);
    }

    /// <summary>A client that did not ask for binary frames gets JSON, with the bytes as base64.</summary>
    [Fact]
    public void JsonPreview_ReadsItsBytesFromBase64()
    {
        const string json = """{"format":"png","width":4,"height":3,"data":"cGljdHVyZQ=="}""";

        var preview = System.Text.Json.JsonSerializer.Deserialize<EncodedImage>(json)!;

        Assert.Equal(("png", 4u, 3u), (preview.Format, preview.Width, preview.Height));
        Assert.Equal("picture"u8.ToArray(), preview.Data);
    }
}
