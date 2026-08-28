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
        Assert.Equal("modified", EntryPresentation.ColumnText(file, "git"));
        Assert.Equal(@"C:\Projects\App\notes.txt", EntryPresentation.ColumnText(file, "path"));
    }
}
