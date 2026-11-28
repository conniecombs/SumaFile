using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>Which pictures SumaFile decodes itself, and through which part of it.</summary>
public class ThumbnailRoutingTests
{
    [Theory]
    [InlineData("jpg")]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("gif")]
    [InlineData("webp")]
    [InlineData("bmp")]
    [InlineData("psd")]
    [InlineData("exr")]
    [InlineData("cr2")]
    [InlineData("svg")]
    public void AppThumbnailsCoverBuiltInsAndMediaFormats(string extension)
    {
        Assert.True(ThumbnailRouting.CanUseAppThumbnail(extension));
    }

    /// <summary>These need codecs SumaFile does not carry; Windows' thumbnail is their only one.</summary>
    [Theory]
    [InlineData("heic")]
    [InlineData("avif")]
    [InlineData("jp2")]
    [InlineData("pdf")]
    [InlineData("mp4")]
    [InlineData("")]
    public void TheRestStaysWithWindows(string extension)
    {
        Assert.False(ThumbnailRouting.CanUseAppThumbnail(extension));
    }

    /// <summary>
    /// A slow Photoshop file must never take one of the slots JPG thumbnails load through, so the formats the
    /// media process decodes have a gate of their own.
    /// </summary>
    [Theory]
    [InlineData("psd", true)]
    [InlineData("nef", true)]
    [InlineData("svg", true)]
    [InlineData("jpg", false)]
    [InlineData("png", false)]
    [InlineData("heic", false)]
    [InlineData("mp4", false)]
    public void OnlyMediaFormatsUseTheMediaGate(string extension, bool expected)
    {
        Assert.Equal(expected, ThumbnailRouting.UsesMediaProcess(extension));
    }

    [Theory]
    [InlineData(@"C:\Art\cover.psd")]
    [InlineData(@"C:\Art\COVER.PSB")]
    [InlineData(@"C:\Renders\scene.exr")]
    [InlineData(@"C:\Raw\IMG_0001.cr2")]
    [InlineData(@"C:\Paintings\study.kra")]
    public void MediaFormatsPreviewThroughTheService(string path)
    {
        Assert.True(ThumbnailRouting.PreviewsThroughService(path));
    }

    [Theory]
    [InlineData(@"C:\Photos\IMG_0001.jpg")]
    [InlineData(@"C:\Photos\IMG_0001.heic")]
    [InlineData(@"C:\Docs\report.pdf")]
    [InlineData(@"C:\Docs\no-extension")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherFilesDoNotPreviewThroughTheService(string? path)
    {
        Assert.False(ThumbnailRouting.PreviewsThroughService(path));
    }

    /// <summary>
    /// WinUI draws an SVG as vectors, sharp at any zoom. The media process would hand back pixels, so it makes
    /// the SVG's thumbnail and leaves its preview alone.
    /// </summary>
    [Fact]
    public void SvgKeepsItsVectorPreview()
    {
        Assert.True(ThumbnailRouting.CanUseAppThumbnail("svg"));
        Assert.False(ThumbnailRouting.PreviewsThroughService(@"C:\Icons\logo.svg"));
    }

    /// <summary>
    /// The thumbnail of an SVG arrives as a JPEG. Read as an SVG because the file is called <c>.svg</c>, it
    /// came out blank: what the bytes are decides how they are read, not the file's name.
    /// </summary>
    [Fact]
    public void EncodedPicturesAreToldFromSvgText()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];
        var gif = "GIF89a..."u8.ToArray();
        var bmp = "BM......"u8.ToArray();
        var webp = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray();
        var svgWithProlog = "<?xml version=\"1.0\"?><svg/>"u8.ToArray();

        Assert.All(new[] { jpeg, png, gif, bmp, webp }, bytes => Assert.True(ThumbnailRouting.IsEncodedPicture(bytes)));
        Assert.False(ThumbnailRouting.IsEncodedPicture(svg));
        Assert.False(ThumbnailRouting.IsEncodedPicture(svgWithProlog));
        Assert.False(ThumbnailRouting.IsEncodedPicture([]));
        Assert.False(ThumbnailRouting.IsEncodedPicture([0xFF]));
    }

    [Fact]
    public void ThePreviewIsAskedForAtScreenSize()
    {
        Assert.Equal(2560u, ThumbnailRouting.PreviewMaxDimension);
    }
}
