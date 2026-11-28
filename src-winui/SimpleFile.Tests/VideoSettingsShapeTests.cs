using Xunit;
using static SimpleFile.Tests.UiAuditSprint3Tests;

namespace SimpleFile.Tests;

/// <summary>
/// The Settings page for video thumbnails is WinUI code that cannot run in a test, so these read it: the switch
/// is there under its exact name, it is loaded from and saved to the setting, and the two lines below it are
/// filled from the file service's counts when the page is shown.
/// </summary>
public class VideoSettingsShapeTests
{
    [Fact]
    public void TheSwitchSitsOnTheStoragePageWithItsTwoLines()
    {
        var xaml = Read("SettingsWindow.xaml");
        var storage = Slice(xaml, "<StackPanel x:Name=\"StoragePanel\"", "<StackPanel x:Name=\"ShortcutsPanel\"");

        Assert.Contains("Text=\"Video thumbnails\"", storage);
        Assert.Contains("x:Name=\"VideoThumbnailsGpuSwitch\"", storage);
        Assert.Contains("Header=\"Use the graphics card for video thumbnails\"", storage);
        Assert.Contains("x:Name=\"VideoThumbnailsCountsText\"", storage);
        Assert.Contains("x:Name=\"VideoThumbnailsReasonText\"", storage);
    }

    [Fact]
    public void TheSwitchIsLoadedAndSaved()
    {
        var state = Read("SettingsWindow.SettingsState.cs");

        Assert.Contains("settings.VideoThumbnailsUseGpu = VideoThumbnailsGpuSwitch.IsOn;", state);
        Assert.Contains("VideoThumbnailsGpuSwitch.IsOn = settings.VideoThumbnailsUseGpu;", state);
    }

    [Fact]
    public void TheLinesAreFilledFromTheFileServiceWhenThePageIsShown()
    {
        var window = Read("SettingsWindow.xaml.cs");
        // A Windows checkout has CRLF line endings: the method's end is looked for in either.
        var state = Read("SettingsWindow.SettingsState.cs").ReplaceLineEndings("\n");
        var refresh = Slice(state, "private async Task RefreshVideoThumbnailStatusAsync()", "\n    }\n");

        Assert.Contains("case \"Storage & Cache\":", window);
        var page = Slice(window, "case \"Storage & Cache\":", "case \"Shortcuts\":");
        Assert.Contains("RefreshVideoThumbnailStatusAsync()", page);
        Assert.Contains("await fileOps.GetMediaStatusAsync(", refresh);
        Assert.Contains("MediaStatusText.Counts(status)", refresh);
        Assert.Contains("MediaStatusText.LastProcessorDecode(status)", refresh);
        // A file service that cannot be asked still gets a line, which claims nothing.
        Assert.Contains("catch", refresh);
    }
}
