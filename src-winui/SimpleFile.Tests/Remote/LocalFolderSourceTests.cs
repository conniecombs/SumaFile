using System.Globalization;
using SumaFile.Remote.Model;
using Xunit;

namespace SimpleFile.Tests.Remote;

public sealed class LocalFolderSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sumafile-local-source-" + Guid.NewGuid().ToString("N"));

    public LocalFolderSourceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task List_PutsFoldersFirstAndKeepsUnicodeNames()
    {
        Directory.CreateDirectory(Path.Combine(_root, "zeta"));
        Directory.CreateDirectory(Path.Combine(_root, "café 資料"));
        File.WriteAllText(Path.Combine(_root, "literal [brackets] & spaces.txt"), "hello");
        File.WriteAllText(Path.Combine(_root, "Alpha.LOG"), "");
        var stamp = new DateTime(2026, 9, 21, 12, 34, 56, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(_root, "literal [brackets] & spaces.txt"), stamp);

        var listing = await new LocalFolderSource().ListAsync(_root, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(_root), listing.Path);
        Assert.False(listing.IsNetwork);
        Assert.Equal(
            ["café 資料", "zeta", "Alpha.LOG", "literal [brackets] & spaces.txt"],
            listing.Entries.Select(entry => entry.Name).ToArray());
        var folder = listing.Entries[0];
        Assert.True(folder.IsDir);
        Assert.Equal(Path.Combine(_root, "café 資料"), folder.Path);
        Assert.Equal("", folder.Extension);
        Assert.Equal(0UL, folder.Size);
        var file = listing.Entries[3];
        Assert.False(file.IsDir);
        Assert.False(file.IsSymlink);
        Assert.Equal(Path.Combine(_root, "literal [brackets] & spaces.txt"), file.Path);
        Assert.Equal(5UL, file.Size);
        Assert.Equal("txt", file.Extension);
        Assert.Equal("LOG", listing.Entries[2].Extension);
        Assert.Equal("2026-09-21T12:34:56Z", file.Modified);
        Assert.Equal(stamp, DateTimeOffset.Parse(file.Modified, CultureInfo.InvariantCulture).UtcDateTime);
    }

    [Fact]
    public async Task List_ReportsTheParent()
    {
        var child = Path.Combine(_root, "café 資料");
        Directory.CreateDirectory(child);
        var source = new LocalFolderSource();

        var listing = await source.ListAsync(child + Path.DirectorySeparatorChar, CancellationToken.None);
        var driveRoot = await source.ListAsync(Path.GetPathRoot(_root)!, CancellationToken.None);

        Assert.Equal(child, listing.Path.TrimEnd(Path.DirectorySeparatorChar));
        Assert.Equal(Path.GetFullPath(_root), listing.Parent);
        Assert.Empty(listing.Entries);
        Assert.Null(driveRoot.Parent);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => source.ListAsync(Path.Combine(_root, "missing"), CancellationToken.None));
    }
}
