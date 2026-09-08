using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class PreviewCapabilitiesTests
{
    [Fact]
    public void Classify_CoversRenderableImagesMediaAndRichMetadata()
    {
        Assert.Equal(
            PreviewCapabilityKind.RenderableHtml,
            PreviewCapabilities.Classify(
                @"C:\notes\readme.md",
                new FilePreview { FileType = "text", MimeType = "text/markdown", Content = "# Notes" }));
        Assert.Equal(
            PreviewCapabilityKind.ImageInline,
            PreviewCapabilities.Classify(
                @"C:\photos\raw.cr3",
                new FilePreview { FileType = "image", MimeType = "image/x-canon-cr3" }));
        Assert.Equal(
            PreviewCapabilityKind.MediaInline,
            PreviewCapabilities.Classify(
                @"C:\video\clip.m2ts",
                new FilePreview { FileType = "video", MimeType = "video/mp2t" }));
        Assert.Equal(
            PreviewCapabilityKind.MetadataRich,
            PreviewCapabilities.Classify(
                @"C:\archive\bundle.zip",
                new FilePreview { FileType = "archive", MimeType = "application/zip" }));
    }

    [Fact]
    public void RenderAndVideoOptions_RequireMatchingSettingsAndFileTypes()
    {
        var markdown = new FilePreview { FileType = "text", MimeType = "text/plain", Content = "# Notes" };
        var video = new FilePreview { FileType = "video", MimeType = "video/mp4" };
        var defaults = UiSettings.CreateDefault();

        Assert.True(PreviewCapabilities.CanRenderAsHtml(@"C:\readme.mdx", markdown));
        Assert.False(PreviewCapabilities.ShouldRenderHtml(defaults, @"C:\readme.mdx", markdown));
        Assert.False(PreviewCapabilities.ShouldPreviewVideo(defaults, video));

        defaults.PreviewRenderHtml = true;
        defaults.PreviewVideoPlaybackEnabled = true;

        Assert.True(PreviewCapabilities.ShouldRenderHtml(defaults, @"C:\readme.mdx", markdown));
        Assert.True(PreviewCapabilities.ShouldPreviewVideo(defaults, video));
        Assert.False(PreviewCapabilities.ShouldPreviewVideo(defaults, new FilePreview { FileType = "audio" }));
    }

    [Fact]
    public void VisualExtensionCatalogs_CoverExpandedFormatsWithoutClaimingTypeScript()
    {
        Assert.True(PhotoFolder.IsImage("photo.avif"));
        Assert.True(PhotoFolder.IsImage("RAW.CR3"));
        Assert.True(PhotoFolder.IsImage(@"D:\Scans\negative.heic"));

        Assert.True(MediaFolder.IsVideo("clip.m2ts"));
        Assert.True(MediaFolder.IsVideo("movie.hevc"));
        Assert.True(MediaFolder.IsVideo(@"D:\Camera\scene.ogv"));
        Assert.False(MediaFolder.IsVideo("app.ts"));
    }
}
