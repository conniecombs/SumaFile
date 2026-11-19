using System.Collections.Generic;
using System.Linq;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class ColumnLayoutTests
{
    [Fact]
    public void SetVisible_TogglesAColumnAndNeverHidesName()
    {
        var columns = new ColumnLayout();
        columns.SetVisible("dimensions", true);
        Assert.Equal(["name", "size", "date", "type", "dimensions"], columns.VisibleIds);
        columns.SetVisible("size", false);
        Assert.Equal(["name", "date", "type", "dimensions"], columns.VisibleIds);
        columns.SetVisible("name", false);
        Assert.Contains("name", columns.VisibleIds);
        columns.SetVisible("dimensions", true);
        Assert.Single(columns.VisibleIds, "dimensions");
    }

    [Fact]
    public void LazyColumns_NameTheirFactKindAndWhichEntriesCanHaveAValue()
    {
        var columns = new ColumnLayout();
        Assert.Equal(LazyColumns.FolderSize, columns.Find("size")!.FactKind);
        Assert.Equal(LazyColumns.ItemCount, columns.Find("items")!.FactKind);
        Assert.Equal(LazyColumns.Dimensions, columns.Find("dimensions")!.FactKind);
        Assert.Equal(LazyColumns.Duration, columns.Find("duration")!.FactKind);
        Assert.Equal(LazyColumns.DateTaken, columns.Find("date_taken")!.FactKind);
        Assert.Equal(LazyColumns.Camera, columns.Find("camera")!.FactKind);
        Assert.Equal(LazyColumns.Pages, columns.Find("pages")!.FactKind);
        Assert.Null(columns.Find("name")!.FactKind);
        Assert.Null(columns.Find("created")!.FactKind);

        var folder = new FileEntry { Name = "src", IsDir = true };
        var photo = new FileEntry { Name = "a.JPG", Extension = "JPG" };
        var clip = new FileEntry { Name = "a.mkv", Extension = "mkv" };
        var song = new FileEntry { Name = "a.flac", Extension = "flac" };
        var paper = new FileEntry { Name = "a.pdf", Extension = "pdf" };
        var text = new FileEntry { Name = "a.txt", Extension = "txt" };
        Assert.True(LazyColumns.CanHaveValue(LazyColumns.FolderSize, folder));
        Assert.False(LazyColumns.CanHaveValue(LazyColumns.FolderSize, photo));
        Assert.True(LazyColumns.CanHaveValue(LazyColumns.ItemCount, folder));
        Assert.True(LazyColumns.CanHaveValue(LazyColumns.Dimensions, photo));
        Assert.False(LazyColumns.CanHaveValue(LazyColumns.Dimensions, clip));
        Assert.True(LazyColumns.CanHaveValue(LazyColumns.Duration, clip));
        Assert.True(LazyColumns.CanHaveValue(LazyColumns.Duration, song));
        Assert.False(LazyColumns.CanHaveValue(LazyColumns.Duration, photo));
        Assert.True(LazyColumns.CanHaveValue(LazyColumns.DateTaken, photo));
        Assert.True(LazyColumns.CanHaveValue(LazyColumns.Camera, photo));
        Assert.True(LazyColumns.CanHaveValue(LazyColumns.Pages, paper));
        Assert.False(LazyColumns.CanHaveValue(LazyColumns.Pages, text));
        Assert.False(LazyColumns.CanHaveValue(LazyColumns.Dimensions, folder));
    }

    [Fact]
    public void LazyColumns_FolderSizeIsOnRequestAndTheRestAutomatic()
    {
        Assert.Equal(LazyColumns.OnRequest, LazyColumns.DefaultMode(LazyColumns.FolderSize));
        foreach (var kind in new[] { LazyColumns.ItemCount, LazyColumns.Dimensions, LazyColumns.Duration, LazyColumns.DateTaken, LazyColumns.Camera, LazyColumns.Pages })
        {
            Assert.Equal(LazyColumns.Automatic, LazyColumns.DefaultMode(kind));
        }

        var modes = LazyColumns.NormalizeModes(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LazyColumns.Dimensions] = "ON-REQUEST",
            [LazyColumns.FolderSize] = "automatic",
            ["unknown"] = "automatic",
            [LazyColumns.Pages] = "sometimes",
        });
        Assert.Equal(new Dictionary<string, string> { [LazyColumns.Dimensions] = LazyColumns.OnRequest, [LazyColumns.FolderSize] = LazyColumns.Automatic }, modes);
        Assert.Equal(LazyColumns.OnRequest, LazyColumns.ModeFor(modes, LazyColumns.Dimensions));
        Assert.Equal(LazyColumns.Automatic, LazyColumns.ModeFor(modes, LazyColumns.Pages));
    }

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

        var wide = columns.EffectiveForPaneWidth(900, dualPane: true, decorationsAvailable: true);
        Assert.Equal(["name", "size", "date", "extension", "git", "symlink", "path"], wide.SnapshotVisibleIds());
        Assert.Equal(420, wide.WidthOf("name"));

        var compact = columns.EffectiveForPaneWidth(520, dualPane: true, decorationsAvailable: true);
        Assert.Equal(["name", "size", "date", "git", "extension", "symlink", "path"], compact.SnapshotVisibleIds());
        Assert.Equal(86, compact.WidthOf("size"));
        Assert.Equal(136, compact.WidthOf("date"));
        Assert.Equal(82, compact.WidthOf("git"));
        Assert.Equal(180, compact.WidthOf("name"));

        var compactWithoutGit = columns.EffectiveForPaneWidth(520, dualPane: true, decorationsAvailable: false);
        Assert.Equal(["name", "size", "date", "extension", "symlink", "path"], compactWithoutGit.SnapshotVisibleIds());
        Assert.Equal(["name", "size", "date", "extension", "git", "symlink", "path"], columns.SnapshotVisibleIds());
        Assert.Equal(420, columns.WidthOf("name"));
    }

    [Fact]
    public void StatusColumn_KeepsTheIdGitWithTheHeaderStatus()
    {
        Assert.Equal("git", ColumnLayout.DecorationColumnId);
        var columns = new ColumnLayout();
        var status = columns.Find(ColumnLayout.DecorationColumnId);

        Assert.NotNull(status);
        Assert.Equal("Status", status!.Label);
        Assert.Equal("git", status.Sort);

        // Saved layouts that name the column by its old id still load.
        columns.RestoreVisibleIds(["name", "git"]);
        Assert.Equal(["name", "git"], columns.SnapshotVisibleIds());
        Assert.Contains("git", ColumnLayout.Presets["developer"]);
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
            ShowTags = true,
        };

        Assert.True(SidebarSectionVisibility.QuickAccess(settings));
        Assert.False(SidebarSectionVisibility.FolderTree(settings, rowCount: 0));
        Assert.True(SidebarSectionVisibility.FolderTree(settings, rowCount: 3));
        Assert.True(SidebarSectionVisibility.Bookmarks(settings));
        Assert.False(SidebarSectionVisibility.Recent(settings, rowCount: 0));
        Assert.True(SidebarSectionVisibility.Recent(settings, rowCount: 2));
        Assert.False(SidebarSectionVisibility.SmartFolders(settings, rowCount: 0, searchActive: false));
        Assert.True(SidebarSectionVisibility.SmartFolders(settings, rowCount: 0, searchActive: true));
        Assert.False(SidebarSectionVisibility.Tags(settings, rowCount: 0, activeTagFilter: false));
        Assert.True(SidebarSectionVisibility.Tags(settings, rowCount: 3, activeTagFilter: false));
        Assert.True(SidebarSectionVisibility.Tags(settings, rowCount: 0, activeTagFilter: true));

        settings.ShowFolderTree = false;
        settings.ShowRecentLocations = false;
        settings.ShowSmartFolders = false;
        settings.ShowTags = false;

        Assert.False(SidebarSectionVisibility.FolderTree(settings, rowCount: 3));
        Assert.False(SidebarSectionVisibility.Recent(settings, rowCount: 2));
        Assert.False(SidebarSectionVisibility.SmartFolders(settings, rowCount: 1, searchActive: true));
        Assert.False(SidebarSectionVisibility.Tags(settings, rowCount: 2, activeTagFilter: true));
    }
}
