using SimpleFile.Ipc;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Core;

public interface IExplorerBackend
{
    string? CachedHomeDir => null;

    IReadOnlyList<DriveInfo>? CachedDrives => null;

    Task<string> GetHomeDirAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DriveInfo>> ListDrivesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DriveInfo>> ListDrivesLightAsync(CancellationToken cancellationToken = default) =>
        ListDrivesAsync(cancellationToken);

    Task<IReadOnlyList<DriveInfo>> ListDriveAsync(string path, CancellationToken cancellationToken = default) =>
        ListDrivesAsync(cancellationToken);

    Task<DirectoryListing> ListDirectoryAsync(
        string path,
        Action<DirectoryListingChunk>? onChunk = null,
        CancellationToken cancellationToken = default,
        ListDirectoryOptions? options = null);
}
