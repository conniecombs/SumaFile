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

    /// <summary>
    /// The backend's listing views, or null when it has none: the workspace then lists each folder whole with
    /// <see cref="ListDirectoryAsync"/> and sorts and filters it itself.
    /// </summary>
    IListingViewBackend? ListingViews => null;

    /// <summary>The backend's lazy column reader, or null when it has none: lazy columns then stay empty.</summary>
    IEntryFactsBackend? EntryFacts => null;

    Task<DirectoryListing> ListDirectoryAsync(
        string path,
        Action<DirectoryListingChunk>? onChunk = null,
        CancellationToken cancellationToken = default,
        ListDirectoryOptions? options = null);
}
