using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

public class UiSettingsTests
{
    [Fact]
    public void UiSettings_NormalizesAppearanceAndStartLocation()
    {
        Assert.Equal("light", UiSettings.NormalizeTheme("Light"));
        Assert.Equal("system", UiSettings.NormalizeTheme("system"));
        Assert.Equal("dark", UiSettings.NormalizeTheme("nope"));
        Assert.Equal("last", UiSettings.NormalizeStartLocation("Last"));
        Assert.Equal("custom", UiSettings.NormalizeStartLocation("custom"));
        Assert.Equal("home", UiSettings.NormalizeStartLocation(null));
        Assert.Equal("details", UiSettings.NormalizeDefaultView("Details"));
        Assert.Equal("tiles", UiSettings.NormalizeDefaultView("tiles"));
        Assert.Equal("details", UiSettings.NormalizeDefaultView("nope"));
        Assert.Equal(16, UiSettings.NormalizeIconSize((int?)null));
        Assert.Equal(32, UiSettings.NormalizeIconSize("33"));
        Assert.Equal(120, UiSettings.NormalizeIconSize(120));
        Assert.Equal(16, UiSettings.NormalizeIconSize(3));
        Assert.Equal(256, UiSettings.NormalizeIconSize(900));
        Assert.Equal(UiSettings.SidebarDefaultWidth, UiSettings.NormalizeSidebarWidth(double.NaN));
        Assert.Equal(UiSettings.SidebarMinWidth, UiSettings.NormalizeSidebarWidth(120));
        Assert.Equal(312, UiSettings.NormalizeSidebarWidth("312"));
        Assert.Equal(UiSettings.SidebarMaxWidth, UiSettings.NormalizeSidebarWidth(900));
        Assert.Equal(UiSettings.PreviewDefaultWidth, UiSettings.NormalizePreviewWidth(double.NaN));
        Assert.Equal(UiSettings.PreviewMinWidth, UiSettings.NormalizePreviewWidth(80));
        Assert.Equal(420, UiSettings.NormalizePreviewWidth("420"));
        Assert.Equal(UiSettings.PreviewMaxWidth, UiSettings.NormalizePreviewWidth(2000));
        Assert.Equal(UiSettings.DualPaneDefaultPercent, UiSettings.NormalizeDualPanePrimaryPercent(double.PositiveInfinity));
        Assert.Equal(UiSettings.DualPaneMinPercent, UiSettings.NormalizeDualPanePrimaryPercent(5));
        Assert.Equal(62, UiSettings.NormalizeDualPanePrimaryPercent("62"));
        Assert.Equal(UiSettings.DualPaneMaxPercent, UiSettings.NormalizeDualPanePrimaryPercent(99));
        Assert.Equal(0, UiSettings.NormalizeDualPanePrimaryWidth(0));
        Assert.Equal(0, UiSettings.NormalizeDualPanePrimaryWidth(double.NaN));
        Assert.Equal(UiSettings.FilePaneMinWidth, UiSettings.NormalizeDualPanePrimaryWidth(40));
        Assert.Equal(420, UiSettings.NormalizeDualPanePrimaryWidth("420"));
        Assert.Equal(400, UiSettings.ResolveDualPanePrimaryWidth(400, 50, 1000));
        Assert.Equal(500, UiSettings.ResolveDualPanePrimaryWidth(0, 50, 1000));
        Assert.Equal(UiSettings.FilePaneMinWidth, UiSettings.ResolveDualPanePrimaryWidth(10, 50, 1000));
        Assert.Equal(1000 - UiSettings.FilePaneMinWidth - UiSettings.DualPaneDividerWidth,
            UiSettings.ResolveDualPanePrimaryWidth(9000, 50, 1000));
    }
    [Fact]
    public void ResolveStartPath_UsesHomeLastAndCustom()
    {
        var backend = FakeExplorerBackend.Typical();
        var workspace = new ExplorerWorkspace(backend);
        workspace.ApplyUiSettings(new UiSettings { StartLocation = "home" });
        // HomePath is empty until Initialize; ResolveStartPath still returns HomePath/primary.
        Assert.Equal("", workspace.ResolveStartPath());

        workspace.ApplyUiSettings(new UiSettings { StartLocation = "custom", CustomPath = @"D:\Work" });
        Assert.Equal(@"D:\Work", workspace.ResolveStartPath());

        workspace.ApplyUiSettings(new UiSettings { StartLocation = "last", LastPath = @"D:\Last" });
        Assert.Equal(@"D:\Last", workspace.ResolveStartPath());
    }
}
