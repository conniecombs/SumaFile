using SimpleFile.Core;
using SimpleFile.Ipc;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

internal sealed class FakeExplorerBackend : IExplorerBackend
{
    public string Home { get; set; } = @"C:\Users\test";
    public List<DriveInfo> Drives { get; } = [];
    public Dictionary<string, DirectoryListing> Listings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Task<DirectoryListing>> Pending { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool EmitChunks { get; set; }
    public bool ThrowTooLargeAfterChunks { get; set; }
    public int ListDirectoryCalls { get; private set; }
    public int ListDrivesCalls { get; private set; }
    public ListDirectoryOptions? LastListDirectoryOptions { get; private set; }
    public Func<CancellationToken, Task<IReadOnlyList<DriveInfo>>>? ListDrivesHandler { get; set; }
    public Func<string, CancellationToken, Task<DirectoryListing>?>? ListDirectoryHandler { get; set; }

    public static FakeExplorerBackend Typical()
    {
        var backend = new FakeExplorerBackend();
        backend.Drives.Add(new DriveInfo
        {
            Name = "Windows (C:)",
            Path = @"C:\",
            DriveType = "Fixed",
            DriveStatus = "available",
            TotalSpace = 100,
            FreeSpace = 40,
        });
        backend.Listings[@"C:\Users\test"] = new DirectoryListing
        {
            Path = @"C:\Users\test",
            Parent = @"C:\Users",
            Entries =
            [
                new FileEntry { Name = "Desktop", Path = @"C:\Users\test\Desktop", IsDir = true },
                new FileEntry { Name = "notes.txt", Path = @"C:\Users\test\notes.txt", Extension = "txt", Size = 12 },
            ],
        };
        backend.Listings[@"C:\Users\test\Desktop"] = new DirectoryListing
        {
            Path = @"C:\Users\test\Desktop",
            Parent = @"C:\Users\test",
            Entries =
            [
                new FileEntry { Name = "shot.png", Path = @"C:\Users\test\Desktop\shot.png", Extension = "png" },
            ],
        };
        backend.Listings[@"C:\"] = new DirectoryListing
        {
            Path = @"C:\",
            Entries =
            [
                new FileEntry { Name = "Users", Path = @"C:\Users", IsDir = true },
            ],
        };
        return backend;
    }

    public Task<string> GetHomeDirAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Home);
    }

    public Task<IReadOnlyList<DriveInfo>> ListDrivesAsync(CancellationToken cancellationToken = default)
    {
        ListDrivesCalls += 1;
        return ListDrivesHandler?.Invoke(cancellationToken)
            ?? Task.FromResult<IReadOnlyList<DriveInfo>>(Drives);
    }

    public async Task<DirectoryListing> ListDirectoryAsync(
        string path,
        Action<DirectoryListingChunk>? onChunk = null,
        CancellationToken cancellationToken = default,
        ListDirectoryOptions? options = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ListDirectoryCalls += 1;
        LastListDirectoryOptions = options;
        var handled = ListDirectoryHandler?.Invoke(path, cancellationToken);
        if (handled is not null)
        {
            return await handled.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Pending.TryGetValue(path, out var pending))
        {
            return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!Listings.TryGetValue(path, out var listing))
        {
            throw new IpcException(Protocol.ErrApplication, $"Path is not a directory: {path}");
        }

        if (EmitChunks || ThrowTooLargeAfterChunks)
        {
            onChunk?.Invoke(new DirectoryListingChunk
            {
                Path = listing.Path,
                Entries = listing.Entries,
                ChunkIndex = 0,
                Done = true,
            });
        }

        if (ThrowTooLargeAfterChunks)
        {
            throw new IpcException(
                Protocol.ErrApplication,
                "RESULT_TOO_LARGE: list_directory result exceeds 80 MiB; use streamed chunks");
        }

        return listing;
    }
}
