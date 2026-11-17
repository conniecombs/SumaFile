using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>
/// A backend that keeps each pane's folder sorted and filtered itself. The workspace then reads the rows in
/// pages (the first page at once, the rest behind it) and applies row changes as they are reported, instead of
/// receiving, sorting and filtering the whole folder on every change.
/// </summary>
public interface IListingViewBackend
{
    /// <summary>
    /// Opens the view of a folder, or re-sorts and re-filters the view when it already shows the folder, and
    /// returns its first page. <paramref name="onPreview"/> receives the first entries of a folder that is still
    /// being enumerated.
    /// </summary>
    Task<ListingViewPage> OpenListingViewAsync(
        ListingViewRequest request,
        Action<DirectoryListingChunk>? onPreview = null,
        CancellationToken cancellationToken = default);

    Task<ListingViewPage> GetListingViewRangeAsync(
        string viewId,
        uint offset,
        uint count,
        CancellationToken cancellationToken = default);

    /// <summary>Enumerates the view's folder again and returns only what changed.</summary>
    Task<ListingViewDelta> RefreshListingViewAsync(string viewId, CancellationToken cancellationToken = default);

    Task CloseListingViewAsync(string viewId, CancellationToken cancellationToken = default);

    /// <summary>Rows of an open view changed (a watched folder changed). Raised on any thread.</summary>
    event Action<ListingViewDelta>? ListingViewChanged;
}
