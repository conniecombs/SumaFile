using System;
using System.IO;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class WindowPlacementTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1040);

    private static ScreenRect Fit(int x, int y, int width, int height, ScreenRect? workArea = null) =>
        new WindowPlacement(new ScreenRect(x, y, width, height), Maximized: false).FitTo(workArea ?? Screen);

    [Fact]
    public void FitTo_LeavesAWindowThatIsOnTheScreenWhereItWas()
    {
        Assert.Equal(new ScreenRect(200, 100, 1280, 840), Fit(200, 100, 1280, 840));
        Assert.Equal(new ScreenRect(0, 0, 1920, 1040), Fit(0, 0, 1920, 1040));
    }

    [Fact]
    public void FitTo_KeepsTheFewPixelsASnappedWindowHangsOverTheEdge()
    {
        // Windows reports a window snapped to the left half with its invisible border outside the screen.
        Assert.Equal(new ScreenRect(-7, 0, 974, 1047), Fit(-7, 0, 974, 1047));
    }

    [Theory]
    [InlineData(-3000, 100, 0, 100)]
    [InlineData(5000, 100, 640, 100)]
    [InlineData(200, -2000, 200, 0)]
    [InlineData(200, 9000, 200, 200)]
    public void FitTo_MovesAWindowFromAScreenThatIsGoneOntoThisOne(int x, int y, int expectedX, int expectedY)
    {
        Assert.Equal(new ScreenRect(expectedX, expectedY, 1280, 840), Fit(x, y, 1280, 840));
    }

    [Fact]
    public void FitTo_ShrinksAWindowLargerThanTheScreen()
    {
        Assert.Equal(new ScreenRect(0, 0, 1920, 1040), Fit(-200, -100, 3840, 2100));
    }

    [Fact]
    public void FitTo_WorksOnAScreenLeftOfThePrimaryOne()
    {
        var left = new ScreenRect(-2560, 0, 2560, 1400);
        Assert.Equal(new ScreenRect(-2000, 200, 1280, 840), Fit(-2000, 200, 1280, 840, left));
        Assert.Equal(new ScreenRect(-1280, 560, 1280, 840), Fit(500, 900, 1280, 840, left));
    }

    [Fact]
    public void FitTo_NeverMakesAWindowSmallerThanTheMinimumUnlessTheScreenIs()
    {
        Assert.Equal(new ScreenRect(100, 100, WindowPlacement.MinWidth, WindowPlacement.MinHeight), Fit(100, 100, 300, 200));
        var tiny = new ScreenRect(0, 0, 500, 300);
        Assert.Equal(new ScreenRect(0, 0, 500, 300), Fit(100, 100, 300, 200, tiny));
    }

    [Fact]
    public void Store_RoundTripsAPlacement()
    {
        var path = TempFile();
        var placement = new WindowPlacement(new ScreenRect(-1500, 40, 1400, 900), Maximized: true);

        Assert.True(WindowPlacementStore.Save(path, placement));

        Assert.Equal(placement, WindowPlacementStore.Load(path));
        Assert.False(File.Exists(path + ".tmp"));
        File.Delete(path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"x\":0,\"y\":0}")]
    [InlineData("{\"x\":0,\"y\":0,\"width\":0,\"height\":600,\"maximized\":false}")]
    [InlineData("{\"x\":0,\"y\":0,\"width\":-800,\"height\":600,\"maximized\":false}")]
    [InlineData("{\"x\":0,\"y\":0,\"width\":800,\"height\":90000,\"maximized\":false}")]
    [InlineData("{\"x\":9999999,\"y\":0,\"width\":800,\"height\":600,\"maximized\":false}")]
    [InlineData("{\"x\":\"left\",\"y\":0,\"width\":800,\"height\":600,\"maximized\":false}")]
    [InlineData("{\"x\":1.5,\"y\":0,\"width\":800,\"height\":600,\"maximized\":false}")]
    public void Store_IgnoresAFileItCannotUse(string content)
    {
        var path = TempFile();
        File.WriteAllText(path, content);

        Assert.Null(WindowPlacementStore.Load(path));
        File.Delete(path);
    }

    [Fact]
    public void Store_TreatsAMissingFileOrFolderAsNoPlacement()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"sumafile-missing-{Guid.NewGuid():N}", "window.json");
        Assert.Null(WindowPlacementStore.Load(missing));
    }

    [Fact]
    public void Store_CreatesItsFolderAndReplacesAnOlderFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"sumafile-placement-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "window.json");
        var first = new WindowPlacement(new ScreenRect(0, 0, 800, 600), Maximized: false);
        var second = new WindowPlacement(new ScreenRect(50, 60, 1000, 700), Maximized: false);

        Assert.True(WindowPlacementStore.Save(path, first));
        Assert.True(WindowPlacementStore.Save(path, second));

        Assert.Equal(second, WindowPlacementStore.Load(path));
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void Store_ReportsAPathItCannotWrite()
    {
        // A file where the folder should be.
        var blocker = TempFile();
        File.WriteAllText(blocker, "x");
        var placement = new WindowPlacement(new ScreenRect(0, 0, 800, 600), Maximized: false);

        Assert.False(WindowPlacementStore.Save(Path.Combine(blocker, "window.json"), placement));
        File.Delete(blocker);
    }

    [Fact]
    public void MainWindow_RestoresBeforeItIsShownAndSavesWhenItCloses()
    {
        var appRoot = Sprint2ShellTests.AppRoot();
        var window = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml.cs"));
        var placement = File.ReadAllText(Path.Combine(appRoot, "MainWindow.Placement.cs"));

        // After the default size, so a first run and an unusable file both leave that size.
        var defaultSize = window.IndexOf("SecondaryWindowChrome.Resize(this, 1280, 840);", StringComparison.Ordinal);
        var restore = window.IndexOf("RestoreWindowPlacement();", StringComparison.Ordinal);
        Assert.True(defaultSize >= 0 && restore > defaultSize);
        var closed = window[window.IndexOf("private async void OnClosed(", StringComparison.Ordinal)..];
        Assert.True(
            closed.IndexOf("SaveWindowPlacement();", StringComparison.Ordinal)
                < closed.IndexOf("await ", StringComparison.Ordinal),
            "the placement is saved before the first await of the close handler");

        // UI smoke runs measure a window of the default size and must not leave a placement behind.
        var restoreBody = placement[placement.IndexOf("private void RestoreWindowPlacement()", StringComparison.Ordinal)..];
        restoreBody = restoreBody[..restoreBody.IndexOf("private void OnAppWindowPlacementChanged(", StringComparison.Ordinal)];
        var saveBody = placement[placement.IndexOf("private void SaveWindowPlacement()", StringComparison.Ordinal)..];
        Assert.Contains("if (UiSmokeRoot() is not null)", restoreBody);
        Assert.Contains("if (UiSmokeRoot() is not null)", saveBody);
        Assert.Contains("DisplayAreaFallback.Nearest", restoreBody);
        Assert.Contains("saved.FitTo(", restoreBody);
    }

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"sumafile-placement-{Guid.NewGuid():N}.json");
}
