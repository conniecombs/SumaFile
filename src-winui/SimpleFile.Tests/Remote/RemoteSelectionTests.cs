using SumaFile.Remote.Model;
using Xunit;

namespace SimpleFile.Tests.Remote;

public class RemoteSelectionTests
{
    private static RemoteFileEntry Entry(string path) => new() { Name = path[(path.LastIndexOf('/') + 1)..], Path = path };

    [Fact]
    public void Carry_ReselectsTheSamePathsInTheNewListingAsRanges()
    {
        RemoteFileEntry[] relisted = [Entry("/a.txt"), Entry("/b.txt"), Entry("/c.txt"), Entry("/new.txt"), Entry("/z.txt")];

        var ranges = RemoteSelection.Carry(["/z.txt", "/b.txt", "/a.txt", "/gone.txt"], relisted, StringComparer.Ordinal);

        Assert.Equal([new RemoteSelectionRange(0, 2), new RemoteSelectionRange(4, 1)], ranges);
    }

    [Fact]
    public void Carry_AnotherFolderOrNoSelectionSelectsNothing()
    {
        RemoteFileEntry[] other = [Entry("/sub/a.txt")];

        Assert.Empty(RemoteSelection.Carry(["/a.txt"], other, StringComparer.Ordinal));
        Assert.Empty(RemoteSelection.Carry([], other, StringComparer.Ordinal));
    }

    [Fact]
    public void Carry_ComparesPathsTheWayTheListsFileSystemDoes()
    {
        RemoteFileEntry[] entries = [new() { Name = "Report.txt", Path = @"C:\Docs\Report.txt" }];

        Assert.Empty(RemoteSelection.Carry([@"c:\docs\report.txt"], entries, StringComparer.Ordinal));
        Assert.Equal([new RemoteSelectionRange(0, 1)], RemoteSelection.Carry([@"c:\docs\report.txt"], entries, StringComparer.OrdinalIgnoreCase));
    }
}
