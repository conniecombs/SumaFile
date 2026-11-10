using System;
using SimpleFile.PluginHost;
using SimpleFile.PluginSdk;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Parked #72/#92: the host checks a plugin's <see cref="PluginWindowOptions"/> before it tracks the window. Sizes are
/// clamped to 320-3840 by 240-2160 DIPs, a blank title falls back to the manifest name, and titles are cut to 120
/// characters.
/// </summary>
public class PluginWindowOptionsPolicyTests
{
    [Theory]
    [InlineData(800, 600, 800, 600)]
    [InlineData(320, 240, 320, 240)]
    [InlineData(3840, 2160, 3840, 2160)]
    [InlineData(319, 239, 320, 240)]
    [InlineData(3841, 2161, 3840, 2160)]
    [InlineData(0, 0, 320, 240)]
    [InlineData(-1, -100, 320, 240)]
    [InlineData(int.MinValue, int.MinValue, 320, 240)]
    [InlineData(int.MaxValue, int.MaxValue, 3840, 2160)]
    [InlineData(int.MaxValue, 0, 3840, 240)]
    public void Normalize_ClampsSizesAndFallsBackToTheManifestName(int width, int height, int expectedWidth, int expectedHeight)
    {
        var normalized = PluginWindowOptionsPolicy.Normalize(new PluginWindowOptions("Title", width, height), "Manifest name");
        Assert.Equal(new PluginWindowOptions("Title", expectedWidth, expectedHeight), normalized);

        foreach (var blank in new[] { null, "", " ", "\t\r\n" })
        {
            var fallback = PluginWindowOptionsPolicy.Normalize(new PluginWindowOptions(blank!, width, height), "Manifest name");
            Assert.Equal(new PluginWindowOptions("Manifest name", expectedWidth, expectedHeight), fallback);
        }
    }

    [Fact]
    public void Normalize_PublishesTheRangesTheSdkDocuments()
    {
        Assert.Equal(320, PluginWindowOptionsPolicy.MinWidth);
        Assert.Equal(3840, PluginWindowOptionsPolicy.MaxWidth);
        Assert.Equal(240, PluginWindowOptionsPolicy.MinHeight);
        Assert.Equal(2160, PluginWindowOptionsPolicy.MaxHeight);
        Assert.Equal(120, PluginWindowOptionsPolicy.MaxTitleLength);
    }

    [Fact]
    public void Normalize_CutsTitlesTo120Characters_WithoutSplittingASurrogatePair()
    {
        var exact = new string('a', 120);
        Assert.Equal(exact, PluginWindowOptionsPolicy.Normalize(new PluginWindowOptions(exact, 800, 600), "N").Title);
        Assert.Equal(exact, PluginWindowOptionsPolicy.Normalize(new PluginWindowOptions(exact + "b", 800, 600), "N").Title);

        // An emoji (two UTF-16 units) straddling the cut is dropped whole, never halved.
        var straddling = new string('a', 119) + "\U0001F600" + "tail";
        var cut = PluginWindowOptionsPolicy.Normalize(new PluginWindowOptions(straddling, 800, 600), "N").Title;
        Assert.Equal(new string('a', 119), cut);

        // A fallback longer than the cap is cut the same way.
        var longName = new string('n', 300);
        Assert.Equal(new string('n', 120), PluginWindowOptionsPolicy.Normalize(new PluginWindowOptions("", 800, 600), longName).Title);
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var once = PluginWindowOptionsPolicy.Normalize(new PluginWindowOptions(new string('x', 400), int.MaxValue, -1), "N");
        Assert.Equal(once, PluginWindowOptionsPolicy.Normalize(once, "Other"));
    }

    [Fact]
    public void Normalize_RejectsMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => PluginWindowOptionsPolicy.Normalize(null!, "N"));
        Assert.ThrowsAny<ArgumentException>(() => PluginWindowOptionsPolicy.Normalize(new PluginWindowOptions("T", 800, 600), null!));
        Assert.ThrowsAny<ArgumentException>(() => PluginWindowOptionsPolicy.Normalize(new PluginWindowOptions("T", 800, 600), " "));
    }

    /// The App's SecondaryWindowChrome.Resize scales the clamped DIPs by the window's DPI. At the largest size and the
    /// highest scale Windows offers (500 %) the physical size stays far inside int.
    [Fact]
    public void Normalize_ClampedSizesCannotOverflowTheDpiScale()
    {
        const double MaxScale = 5.0;
        Assert.True(PluginWindowOptionsPolicy.MaxWidth * MaxScale < int.MaxValue);
        Assert.True(PluginWindowOptionsPolicy.MaxHeight * MaxScale < int.MaxValue);
        var normalized = PluginWindowOptionsPolicy.Normalize(new PluginWindowOptions("T", int.MaxValue, int.MaxValue), "N");
        Assert.Equal(19200, checked((int)Math.Round(normalized.Width * MaxScale)));
        Assert.Equal(10800, checked((int)Math.Round(normalized.Height * MaxScale)));
    }
}
