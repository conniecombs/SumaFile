using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class NewItemTemplateTests
{
    [Fact]
    public void SuggestedName_UsesTemplateNameWhenAvailable()
    {
        var entries = new[]
        {
            new FileEntry { Name = "notes.txt" },
        };

        Assert.Equal("New Text Document.txt", NewItemTemplate.TextFile.SuggestedName(entries));
        Assert.Equal("New File", NewItemTemplate.EmptyFile.SuggestedName(entries));
        Assert.Equal("New Shortcut.lnk", NewItemTemplate.Shortcut.SuggestedName(entries));
    }

    [Fact]
    public void SuggestedName_AddsSuffixWhenTemplateNameExists()
    {
        var entries = new[]
        {
            new FileEntry { Name = "New Text Document.txt" },
            new FileEntry { Name = "new text document (2).txt" },
        };

        Assert.Equal("New Text Document (3).txt", NewItemTemplate.TextFile.SuggestedName(entries));
    }

    [Fact]
    public void RenameSelectionLength_SelectsBaseNameForFiles()
    {
        Assert.Equal("New Text Document".Length, NewItemTemplate.RenameSelectionLength("New Text Document.txt", isDirectory: false));
        Assert.Equal("New Folder".Length, NewItemTemplate.RenameSelectionLength("New Folder", isDirectory: true));
    }

    [Fact]
    public void SuggestedShortcutName_UsesTargetNameAndAvoidsCollisions()
    {
        var entries = new[]
        {
            new FileEntry { Name = "notes.lnk", Path = @"C:\Users\test\notes.lnk" },
            new FileEntry { Name = "notes (2).lnk", Path = @"C:\Users\test\notes (2).lnk" },
        };

        Assert.Equal("notes (3).lnk", NewItemTemplate.SuggestedShortcutName(@"C:\Users\test\notes.txt", entries));
    }
}
