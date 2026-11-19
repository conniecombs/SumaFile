using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class EntryPresentationTests
{
    [Fact]
    public void FormatFileSize_DirectoriesAreBlank()
    {
        Assert.Equal("", EntryPresentation.FormatFileSize(12, isDirectory: true));
        Assert.Equal("0 B", EntryPresentation.FormatFileSize(0));
        Assert.Equal("1.0 KB", EntryPresentation.FormatFileSize(1024));
    }

    [Fact]
    public void FormatCompactFileSize_UsesShortFractions()
    {
        Assert.Equal("0 B", EntryPresentation.FormatCompactFileSize(0UL));
        Assert.Equal("1 KB", EntryPresentation.FormatCompactFileSize(1024UL));
        Assert.Equal("1.5 KB", EntryPresentation.FormatCompactFileSize(1536UL));
        Assert.Equal("0 B", EntryPresentation.FormatCompactFileSize(-10L));
    }

    [Fact]
    public void FileType_FolderOrExtension()
    {
        Assert.Equal("Folder", EntryPresentation.FileType(new FileEntry { IsDir = true, Name = "src" }));
        Assert.Equal("TXT File", EntryPresentation.FileType(new FileEntry { Name = "a.txt", Extension = "txt" }));
        Assert.Equal("File", EntryPresentation.FileType(new FileEntry { Name = "LICENSE" }));
    }

    [Fact]
    public void VisibleEntries_HidesDotfilesAndSortsDirsFirst()
    {
        FileEntry[] entries =
        [
            new() { Name = "zebra.txt", IsDir = false, Path = @"C:\z" },
            new() { Name = ".hidden", IsDir = false, Path = @"C:\h" },
            new() { Name = "alpha", IsDir = true, Path = @"C:\a" },
            new() { Name = "beta", IsDir = true, Path = @"C:\b" },
        ];

        var visible = EntryPresentation.VisibleEntries(entries);
        Assert.Equal(["alpha", "beta", "zebra.txt"], visible.Select(entry => entry.Name));
    }

    [Fact]
    public void VisibleEntries_HidesWindowsHiddenAndSystemAttributes()
    {
        FileEntry[] entries =
        [
            new() { Name = "desktop.ini", Path = @"C:\desktop.ini", IsHidden = true, IsSystem = true },
            new() { Name = "notes.txt", Path = @"C:\notes.txt" },
            new() { Name = "secret.txt", Path = @"C:\secret.txt", IsHidden = true },
        ];

        var hidden = EntryPresentation.VisibleEntries(entries, showHidden: false);
        Assert.Equal("notes.txt", Assert.Single(hidden).Name);

        var shown = EntryPresentation.VisibleEntries(entries, showHidden: true);
        Assert.Equal(["desktop.ini", "notes.txt", "secret.txt"], shown.Select(entry => entry.Name));
    }

    [Fact]
    public void SortEntries_CanMixFoldersWithFiles()
    {
        FileEntry[] entries =
        [
            new() { Name = "zoo", IsDir = true, Path = @"C:\zoo" },
            new() { Name = "alpha.txt", IsDir = false, Path = @"C:\alpha.txt" },
            new() { Name = "beta", IsDir = true, Path = @"C:\beta" },
        ];

        var grouped = EntryPresentation.SortEntries(entries, keepFoldersOnTop: true);
        Assert.Equal(["beta", "zoo", "alpha.txt"], grouped.Select(entry => entry.Name));

        var mixed = EntryPresentation.SortEntries(entries, keepFoldersOnTop: false);
        Assert.Equal(["alpha.txt", "beta", "zoo"], mixed.Select(entry => entry.Name));
    }

    [Fact]
    public void VisibleEntries_FilterIsCaseInsensitive()
    {
        FileEntry[] entries =
        [
            new() { Name = "ReadMe.md", Path = @"C:\r" },
            new() { Name = "notes.txt", Path = @"C:\n" },
        ];

        var visible = EntryPresentation.VisibleEntries(entries, filterQuery: "readme");
        Assert.Equal("ReadMe.md", Assert.Single(visible).Name);
    }

    [Fact]
    public void ColumnText_ShowsTheFreeColumnsAndSortEntriesOrdersThem()
    {
        var older = new FileEntry { Name = "older.txt", Path = @"C:\older.txt", Created = "2026-01-01 09:00", Accessed = "2026-03-01 09:00", Attributes = "RA" };
        var newer = new FileEntry { Name = "newer.txt", Path = @"C:\newer.txt", Created = "2026-02-01 09:00", Accessed = "2026-01-01 09:00", Attributes = "A" };
        var unknown = new FileEntry { Name = "unknown.txt", Path = @"C:\unknown.txt" };

        Assert.Equal(EntryPresentation.FormatModified("2026-01-01 09:00"), EntryPresentation.ColumnText(older, "created"));
        Assert.Equal(EntryPresentation.FormatModified("2026-03-01 09:00"), EntryPresentation.ColumnText(older, "accessed"));
        Assert.Equal("RA", EntryPresentation.ColumnText(older, "attributes"));
        Assert.Equal("", EntryPresentation.ColumnText(unknown, "created"));
        Assert.Equal("", EntryPresentation.ColumnText(unknown, "attributes"));

        string[] Names(string column) => [.. EntryPresentation.SortEntries([older, newer, unknown], column).Select(entry => entry.Name)];
        Assert.Equal(["unknown.txt", "older.txt", "newer.txt"], Names("created"));
        Assert.Equal(["unknown.txt", "newer.txt", "older.txt"], Names("accessed"));
        Assert.Equal(["unknown.txt", "newer.txt", "older.txt"], Names("attributes"));
    }

    [Fact]
    public void ColumnLayout_OffersTheFreeColumns()
    {
        var columns = new ColumnLayout();
        Assert.Equal(("Created", "created"), (columns.Find("created")!.Label, columns.Find("created")!.Sort));
        Assert.Equal(("Accessed", "accessed"), (columns.Find("accessed")!.Label, columns.Find("accessed")!.Sort));
        Assert.Equal(("Attributes", "attributes"), (columns.Find("attributes")!.Label, columns.Find("attributes")!.Sort));
        // Not in any preset: the picker adds them.
        Assert.DoesNotContain("created", ColumnLayout.Presets.Values.SelectMany(ids => ids));
    }

    [Fact]
    public void ColumnText_FormatsExtraColumns()
    {
        var folder = new FileEntry
        {
            Name = "src",
            Path = @"C:\Projects\App\src",
            IsDir = true,
            Size = 4096,
            ItemCount = 42,
            SymlinkTarget = @"D:\Shared\src",
        };
        Assert.Equal("4.0 KB", EntryPresentation.ColumnText(folder, "size"));
        Assert.Equal("42 items", EntryPresentation.ColumnText(folder, "items"));
        Assert.Equal(@"D:\Shared\src", EntryPresentation.ColumnText(folder, "symlink"));
        Assert.Equal(@"C:\Projects\App", EntryPresentation.ColumnText(folder, "parent"));

        var file = new FileEntry
        {
            Name = "notes.txt",
            Path = @"C:\Projects\App\notes.txt",
            Extension = "txt",
            GitStatus = "modified",
        };
        Assert.Equal(".txt", EntryPresentation.ColumnText(file, "extension"));
        // The retired FileEntry.GitStatus is never shown: the status column draws the plugin decoration.
        Assert.Equal("", EntryPresentation.ColumnText(file, ColumnLayout.DecorationColumnId));
        Assert.Equal(@"C:\Projects\App\notes.txt", EntryPresentation.ColumnText(file, "path"));
    }

    [Fact]
    public void ContentSecondaryText_AvoidsRepeatingFullFilePathAndFormatsDecorationAndSymlink()
    {
        Assert.Equal(
            "modified  •  → D:\\Target\\notes.txt",
            EntryPresentation.ContentSecondaryText(
                parentText: @"C:\Projects\App",
                decorationText: "modified",
                symlinkText: @"D:\Target\notes.txt",
                extensionText: ".txt"));

        Assert.Equal(
            @"C:\Projects\App",
            EntryPresentation.ContentSecondaryText(
                parentText: @"C:\Projects\App",
                decorationText: "",
                symlinkText: "",
                extensionText: ".txt"));
    }

    [Fact]
    public void ContentSecondaryText_ShowsTheDecorationTextAsIs()
    {
        // The host adds no "Git:" prefix: the plugin's text is the whole label.
        Assert.Equal(
            "M",
            EntryPresentation.ContentSecondaryText(parentText: null, decorationText: "  M ", symlinkText: null));
        Assert.Equal(
            "Git: modified",
            EntryPresentation.ContentSecondaryText(parentText: null, decorationText: "Git: modified", symlinkText: null));
        Assert.Equal(
            @"staged  •  C:\Projects",
            EntryPresentation.ContentSecondaryText(
                parentText: @"C:\Projects",
                decorationText: "staged",
                symlinkText: null,
                includeParent: true));
    }
}
