using System;
using System.IO;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class PreviewPathSupportTests
{
    [Fact]
    public void IsPathBackedPreviewType_CoversPdfAudioAndVideoOnly()
    {
        Assert.True(PreviewPathSupport.IsPathBackedPreviewType("pdf"));
        Assert.True(PreviewPathSupport.IsPathBackedPreviewType("AUDIO"));
        Assert.True(PreviewPathSupport.IsPathBackedPreviewType("video"));

        Assert.False(PreviewPathSupport.IsPathBackedPreviewType("image"));
        Assert.False(PreviewPathSupport.IsPathBackedPreviewType("text"));
        Assert.False(PreviewPathSupport.IsPathBackedPreviewType("archive"));
        Assert.False(PreviewPathSupport.IsPathBackedPreviewType(null));
    }

    [Fact]
    public void CanUsePathBackedPreview_RequiresExistingFilesystemPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sumafile-preview-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, "%PDF-1.7");

        try
        {
            Assert.True(PreviewPathSupport.CanUsePathBackedPreview(path, "pdf"));
            Assert.False(PreviewPathSupport.CanUsePathBackedPreview(path, "image"));
            Assert.False(PreviewPathSupport.CanUsePathBackedPreview(path + ".missing", "pdf"));
            Assert.False(PreviewPathSupport.CanUsePathBackedPreview("", "video"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
