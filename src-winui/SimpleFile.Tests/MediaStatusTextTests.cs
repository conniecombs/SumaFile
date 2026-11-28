using System.Globalization;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// The lines under "Use the graphics card for video thumbnails" in Settings. They are how the user sees whether
/// the graphics card really decodes: nothing else can say.
/// </summary>
public class MediaStatusTextTests
{
    private const string NothingYet =
        "No video thumbnail has been decoded since SumaFile started. Thumbnails already in the cache are not decoded again.";

    [Fact]
    public void BeforeAnyDecodeTheLineSaysSo()
    {
        Assert.Equal(NothingYet, MediaStatusText.Counts(new MediaStatus()));
        Assert.Null(MediaStatusText.LastProcessorDecode(new MediaStatus()));
    }

    /// <summary>The file service could not be asked: the page still shows a line, and claims nothing.</summary>
    [Fact]
    public void AStatusThatCouldNotBeReadSaysTheSame()
    {
        Assert.Equal(NothingYet, MediaStatusText.Counts(null));
        Assert.Null(MediaStatusText.LastProcessorDecode(null));
    }

    [Fact]
    public void TheCountsAreSpelledOut()
    {
        var status = new MediaStatus { VideoHardware = 212, VideoSoftware = 3 };

        Assert.Equal(
            "Since SumaFile started: 212 decoded by the graphics card, 3 by the processor.",
            MediaStatusText.Counts(status));
    }

    [Fact]
    public void LargeCountsAreGrouped()
    {
        var status = new MediaStatus { VideoHardware = 1234, VideoSoftware = 0 };

        Assert.Equal(
            "Since SumaFile started: 1,234 decoded by the graphics card, 0 by the processor.",
            MediaStatusText.Counts(status, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("turned_off", "Last processor decode: the graphics card is turned off above.")]
    [InlineData("no_device", "Last processor decode: Windows found no graphics card that can decode video.")]
    [InlineData("no_hardware_decoder", "Last processor decode: the graphics card has no decoder for that video's format.")]
    [InlineData("hardware_failed", "Last processor decode: hardware decoding failed for that video.")]
    public void TheLastProcessorDecodeSaysWhy(string reason, string line)
    {
        var status = new MediaStatus { VideoSoftware = 1, LastSoftwareReason = reason };

        Assert.Equal(line, MediaStatusText.LastProcessorDecode(status));
    }

    /// <summary>A newer file service may know a reason this app does not: no line, not a wrong one.</summary>
    [Theory]
    [InlineData("something_new")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownReasonGivesNoSecondLine(string? reason)
    {
        var status = new MediaStatus { VideoSoftware = 4, LastSoftwareReason = reason };

        Assert.Null(MediaStatusText.LastProcessorDecode(status));
    }
}
