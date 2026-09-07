using System.Collections.Generic;
using System.Linq;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class ColumnLayoutTests
{
    [Fact]
    public void ColumnLayout_ClampsResizeAndAppliesPresets()
    {
        var columns = new ColumnLayout();
        columns.Resize("size", 10);
        Assert.Equal(36, columns.WidthOf("size"));
        columns.Resize("size", 800);
        Assert.Equal(800, columns.WidthOf("size"));
        columns.Resize("name", 40);
        Assert.Equal(80, columns.WidthOf("name"));
        columns.Resize("name", 500);
        Assert.Equal(500, columns.WidthOf("name"));
        Assert.Equal(columns.VisibleColumns.Sum(column => column.Width), columns.VisibleWidth);
        columns.ApplyPreset("developer");
        Assert.Contains("git", columns.VisibleIds);
        Assert.Equal(["name", "size", "date", "extension", "git", "symlink", "path"], columns.VisibleColumns.Select(column => column.Id));
        columns.RestoreWidths(new Dictionary<string, double> { ["name"] = 300 });
        Assert.Equal(300, columns.WidthOf("name"));
    }

    [Fact]
    public void ColumnLayout_RestoresExplicitVisibleColumns()
    {
        var columns = new ColumnLayout();
        columns.ApplyPreset("developer");

        columns.RestoreVisibleIds(["name", "git", "name", "missing", "path"]);

        Assert.Equal(["name", "git", "path"], columns.SnapshotVisibleIds());
        Assert.Equal(["name", "git", "path"], columns.VisibleColumns.Select(column => column.Id));

        columns.RestoreVisibleIds([]);
        Assert.Equal(["name", "git", "path"], columns.SnapshotVisibleIds());
    }

    [Fact]
    public void PrimaryAndSecondaryColumnLayouts_AreIndependent()
    {
        var primary = new ColumnLayout();
        var secondary = new ColumnLayout();

        primary.Resize("name", 300);
        primary.ApplyPreset("developer");
        primary.RestoreVisibleIds(["name", "git"]);

        Assert.Equal(240, secondary.WidthOf("name"));
        Assert.Equal(ColumnLayout.DefaultVisible, secondary.SnapshotVisibleIds());
        Assert.DoesNotContain("git", secondary.VisibleIds);

        secondary.Resize("size", 180);
        secondary.ApplyPreset("photo");

        Assert.Equal(300, primary.WidthOf("name"));
        Assert.Equal(["name", "git"], primary.SnapshotVisibleIds());
        Assert.Equal(180, secondary.WidthOf("size"));
        Assert.Contains("date", secondary.VisibleIds);
        Assert.DoesNotContain("git", secondary.VisibleIds);
    }

    [Fact]
    public void EffectiveForPaneWidth_UsesCompactProjectionWithoutChangingSavedLayout()
    {
        var columns = new ColumnLayout();
        columns.ApplyPreset("developer");
        columns.Resize("name", 420);

        var wide = columns.EffectiveForPaneWidth(900, dualPane: true, gitEnabled: true);
        Assert.Equal(["name", "size", "date", "extension", "git", "symlink", "path"], wide.SnapshotVisibleIds());
        Assert.Equal(420, wide.WidthOf("name"));

        var compact = columns.EffectiveForPaneWidth(520, dualPane: true, gitEnabled: true);
        Assert.Equal(["name", "size", "date", "git"], compact.SnapshotVisibleIds());
        Assert.Equal(86, compact.WidthOf("size"));
        Assert.Equal(136, compact.WidthOf("date"));
        Assert.Equal(82, compact.WidthOf("git"));
        Assert.Equal(180, compact.WidthOf("name"));

        var compactWithoutGit = columns.EffectiveForPaneWidth(520, dualPane: true, gitEnabled: false);
        Assert.Equal(["name", "size", "date"], compactWithoutGit.SnapshotVisibleIds());
        Assert.Equal(["name", "size", "date", "extension", "git", "symlink", "path"], columns.SnapshotVisibleIds());
        Assert.Equal(420, columns.WidthOf("name"));
    }

    [Fact]
    public void SidebarSectionVisibility_HidesEmptyOptionalSections()
    {
        var settings = new UiSettings
        {
            ShowQuickAccess = true,
            ShowFolderTree = true,
            ShowBookmarks = true,
            ShowRecentLocations = true,
            ShowSmartFolders = true,
        };

        Assert.True(SidebarSectionVisibility.QuickAccess(settings));
        Assert.False(SidebarSectionVisibility.FolderTree(settings, rowCount: 0));
        Assert.True(SidebarSectionVisibility.FolderTree(settings, rowCount: 3));
        Assert.True(SidebarSectionVisibility.Bookmarks(settings));
        Assert.False(SidebarSectionVisibility.Recent(settings, rowCount: 0));
        Assert.True(SidebarSectionVisibility.Recent(settings, rowCount: 2));
        Assert.False(SidebarSectionVisibility.SmartFolders(settings, rowCount: 0, searchActive: false));
        Assert.True(SidebarSectionVisibility.SmartFolders(settings, rowCount: 0, searchActive: true));

        settings.ShowFolderTree = false;
        settings.ShowRecentLocations = false;
        settings.ShowSmartFolders = false;

        Assert.False(SidebarSectionVisibility.FolderTree(settings, rowCount: 3));
        Assert.False(SidebarSectionVisibility.Recent(settings, rowCount: 2));
        Assert.False(SidebarSectionVisibility.SmartFolders(settings, rowCount: 1, searchActive: true));
    }
}
