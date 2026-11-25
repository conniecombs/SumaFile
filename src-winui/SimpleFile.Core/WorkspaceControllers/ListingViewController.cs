using System.Globalization;
using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>
/// Keeps each pane's rows in step with the view the service holds for it. A navigation publishes the view's first
/// page; this controller reads the remaining rows behind it, asks the service to re-sort or re-filter when the
/// pane's order, filter or hidden-file setting changes, and applies the row changes the service reports. The pane
/// never receives a folder it has to sort or filter itself, and a change never costs a second listing.
/// </summary>
internal sealed class ListingViewController
{
    /// <summary>The rows a navigation shows at once.</summary>
    public const uint FirstPageRows = 512;

    /// <summary>The rows each later request reads.</summary>
    public const uint FillPageRows = 4096;

    /// <summary>Row changes kept while a navigation is still publishing; more than this is read again instead.</summary>
    private const int MaxPendingDeltas = 64;
    private const int MaxRenamedPaths = 512;

    /// <summary>Attempts to read a page whose revision the pane has not caught up with yet.</summary>
    private const int MaxFillRetries = 40;

    private static readonly TimeSpan FillRetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>The view a pane wants for <paramref name="path"/>: its sort, the hidden-file setting and its filter.</summary>
    public static ListingViewRequest BuildRequest(ExplorerWorkspace workspace, PaneId paneId, string path, bool reload = false)
    {
        var pane = workspace.Pane(paneId);
        var serviceSorts = PaneListing.IsServiceSortColumn(pane.SortBy, workspace.Settings.ShowFolderSizes);
        return new ListingViewRequest
        {
            ViewId = pane.ListingViewId,
            Path = path,
            // A column the service cannot sort by is sorted by the pane, over rows the service orders by name.
            SortBy = serviceSorts ? PaneListing.ServiceSortColumn(pane.SortBy) : "name",
            SortAscending = !serviceSorts || pane.SortAscending,
            FoldersFirst = workspace.Settings.KeepFoldersOnTop,
            IncludeHidden = workspace.ShowHiddenFiles,
            Filter = workspace.FilterQueryFor(paneId) ?? "",
            Locale = CultureInfo.CurrentCulture.Name,
            PageSize = FirstPageRows,
            Reload = reload,
        };
    }

    /// <summary>Under the gate, when a navigation starts: the rows of the folder being left stop following its view.</summary>
    public static void DetachLocked(ExplorerPane pane)
    {
        pane.Listing = null;
        pane.ListingEpoch++;
        pane.ListingViewUnavailable = false;
        pane.PendingListingDeltas.Clear();
    }

    /// <summary>
    /// Under the gate, with the navigation's rows just published: makes <paramref name="page"/> the pane's view (or
    /// leaves the pane without one when the folder was listed whole) and applies the row changes that arrived while
    /// the navigation was publishing. Returns true when rows remain to be read; the caller then keeps the pane
    /// listing and calls <see cref="StartFill"/>.
    /// </summary>
    public static bool InstallLocked(ExplorerWorkspace workspace, ExplorerPane pane, ListingViewRequest? request, ListingViewPage? page)
    {
        pane.ListingEpoch++;
        var pending = pane.PendingListingDeltas.ToArray();
        pane.PendingListingDeltas.Clear();
        if (request is null || page is null)
        {
            pane.Listing = null;
            return false;
        }

        pane.ListingSortsFolderSizes = workspace.Settings.ShowFolderSizes;
        var listing = new PaneListing
        {
            ViewId = request.ViewId,
            Path = page.Path,
            Request = request,
            Revision = page.Revision,
            Total = Math.Max(ClampToInt(page.Total), pane.Entries.Count),
        };
        pane.Listing = listing;
        foreach (var delta in pending.OrderBy(delta => delta.Revision))
        {
            ApplyDeltaLocked(pane, delta);
        }

        return pane.Entries.Count < listing.Total || listing.NeedsReload;
    }

    /// <summary>
    /// Reads the rows after the ones the pane holds, page by page, and publishes the complete folder once. A row
    /// change that arrives meanwhile is applied to the rows held; a page read across it is read again.
    /// </summary>
    public void StartFill(ExplorerWorkspace workspace, PaneId paneId)
    {
        var pane = workspace.Pane(paneId);
        int epoch;
        lock (workspace.Gate)
        {
            if (pane.Listing is null)
            {
                return;
            }

            epoch = pane.ListingEpoch;
        }

        _ = Task.Run(() => FillAsync(workspace, paneId, epoch));
    }

    private async Task FillAsync(ExplorerWorkspace workspace, PaneId paneId, int epoch)
    {
        var pane = paneId == PaneId.Secondary ? workspace.Secondary : workspace.Primary;
        if (workspace.Backend.ListingViews is not { } views)
        {
            return;
        }

        var retries = 0;
        List<FileEntry>? staged = null;
        ulong stagedRevision = 0;
        try
        {
            while (true)
            {
                string viewId;
                uint offset;
                lock (workspace.Gate)
                {
                    if (pane.Listing is not { } listing || pane.ListingEpoch != epoch)
                    {
                        return;
                    }

                    if (listing.NeedsReload)
                    {
                        break;
                    }

                    if (staged is null || stagedRevision != listing.Revision)
                    {
                        var initialCapacity = Math.Max(
                            pane.Entries.Count + (int)FillPageRows,
                            Math.Min(listing.Total, 262_144));
                        staged = new List<FileEntry>(initialCapacity);
                        staged.AddRange(pane.Entries);
                        stagedRevision = listing.Revision;
                    }

                    if (staged.Count >= listing.Total)
                    {
                        pane.Entries = staged;
                        listing.Total = pane.Entries.Count;
                        pane.ListingInProgress = false;
                        pane.BumpContentVersion();
                        goto filled;
                    }

                    viewId = listing.ViewId;
                    offset = (uint)staged.Count;
                }

                var page = await views.GetListingViewRangeAsync(viewId, offset, FillPageRows).ConfigureAwait(false);
                lock (workspace.Gate)
                {
                    if (pane.Listing is not { } listing || pane.ListingEpoch != epoch)
                    {
                        return;
                    }

                    if (staged is not null
                        && stagedRevision == listing.Revision
                        && page.Revision == listing.Revision
                        && page.Offset == staged.Count)
                    {
                        retries = 0;
                        if (page.Entries.Count == 0)
                        {
                            // The view ends here, whatever total was last reported.
                            listing.Total = staged.Count;
                        }
                        else
                        {
                            staged.AddRange(page.Entries);
                            listing.Total = Math.Max(ClampToInt(page.Total), staged.Count);
                        }

                        continue;
                    }

                    // The page was read at another revision than the pane holds: a row change is on its way (or
                    // has just landed). Read the page again once the pane has it.
                    if (++retries > MaxFillRetries)
                    {
                        listing.NeedsReload = true;
                        break;
                    }
                }

                await Task.Delay(FillRetryDelay).ConfigureAwait(false);
            }

            await ReloadAsync(workspace, paneId, epoch).ConfigureAwait(false);
            return;

        filled:
            workspace.NotifyChanged();
            workspace.StartDecorationRefresh(paneId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The service went away or the view is gone: show the folder whole rather than half of it.
            await FallBackAsync(workspace, paneId, epoch).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asks the service to re-sort or re-filter each pane whose order, filter or hidden-file setting no longer
    /// matches its view. Called after every workspace change; it does nothing when the views already match.
    /// </summary>
    public void Reconcile(ExplorerWorkspace workspace)
    {
        if (workspace.Backend.ListingViews is null)
        {
            return;
        }

        ReconcilePane(workspace, PaneId.Primary);
        if (workspace.DualPaneEnabled)
        {
            ReconcilePane(workspace, PaneId.Secondary);
        }
    }

    private void ReconcilePane(ExplorerWorkspace workspace, PaneId paneId)
    {
        var pane = workspace.Pane(paneId);
        ListingViewRequest desired;
        int epoch;
        lock (workspace.Gate)
        {
            if (pane.Listing is not { } listing || pane.IsNavigating)
            {
                return;
            }

            pane.ListingSortsFolderSizes = workspace.Settings.ShowFolderSizes;
            desired = BuildRequest(workspace, paneId, listing.Path);
            if (listing.ShowsOrAwaits(desired))
            {
                return;
            }

            listing.Pending = desired;
            epoch = pane.ListingEpoch;
        }

        _ = Task.Run(() => ReopenAsync(workspace, paneId, epoch, desired));
    }

    /// <summary>Reads the view's first page again and fills behind it (a row change could not be applied).</summary>
    private Task ReloadAsync(ExplorerWorkspace workspace, PaneId paneId, int epoch)
    {
        var pane = workspace.Pane(paneId);
        ListingViewRequest request;
        lock (workspace.Gate)
        {
            if (pane.Listing is not { } listing || pane.ListingEpoch != epoch)
            {
                return Task.CompletedTask;
            }

            request = listing.Pending ?? listing.Request;
        }

        return ReopenAsync(workspace, paneId, epoch, request);
    }

    /// <summary>
    /// Opens the pane's view again with <paramref name="request"/>: for the folder it already shows the service
    /// only re-sorts and re-filters. The first page replaces the pane's rows; the rest is read behind it.
    /// </summary>
    private async Task ReopenAsync(ExplorerWorkspace workspace, PaneId paneId, int epoch, ListingViewRequest request)
    {
        var pane = workspace.Pane(paneId);
        if (workspace.Backend.ListingViews is not { } views)
        {
            return;
        }

        try
        {
            // Not a navigation: the service only re-sorts the view it holds, and leaves alone a navigation the
            // pane may have started since (whose answer then replaces these rows anyway).
            var page = await views.OpenListingViewAsync(request.ForTheViewShown()).ConfigureAwait(false);
            bool partial;
            lock (workspace.Gate)
            {
                if (pane.Listing is not { } listing || pane.ListingEpoch != epoch)
                {
                    return;
                }

                pane.Entries = [.. page.Entries];
                listing.Request = request;
                // Nothing is awaited any more. A newer request that was still in flight is dropped by the epoch
                // change below, so if the pane wants another order by now, the reconcile after this publish asks
                // for it again.
                listing.Pending = null;

                listing.Revision = page.Revision;
                listing.Total = Math.Max(ClampToInt(page.Total), pane.Entries.Count);
                listing.NeedsReload = false;
                pane.ListingEpoch = epoch + 1;
                partial = pane.Entries.Count < listing.Total;
                pane.ListingInProgress = partial;
                pane.BumpContentVersion();
                if (!partial)
                {
                }
            }

            workspace.NotifyChanged();
            if (partial)
            {
                StartFill(workspace, paneId);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await FallBackAsync(workspace, paneId, epoch).ConfigureAwait(false);
        }
    }

    /// <summary>The view could not be read: drop it and list the folder whole, as a pane without views does.</summary>
    private static async Task FallBackAsync(ExplorerWorkspace workspace, PaneId paneId, int epoch)
    {
        var pane = workspace.Pane(paneId);
        lock (workspace.Gate)
        {
            if (pane.Listing is null || pane.ListingEpoch != epoch)
            {
                return;
            }

            pane.Listing = null;
            pane.ListingEpoch++;
            pane.ListingViewUnavailable = true;
            pane.PendingListingDeltas.Clear();
            pane.ListingInProgress = false;
        }

        try
        {
            await workspace.RefreshAsync(paneId).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // RefreshAsync reports its own failures in the workspace's error message.
        }
    }

    /// <summary>A row change the service reported for one of its views (any thread).</summary>
    public void OnDelta(ExplorerWorkspace workspace, ListingViewDelta delta)
    {
        var paneId = string.Equals(workspace.Pane(PaneId.Primary).ListingViewId, delta.ViewId, StringComparison.Ordinal)
            ? PaneId.Primary
            : PaneId.Secondary;
        var pane = workspace.Pane(paneId);
        if (!string.Equals(pane.ListingViewId, delta.ViewId, StringComparison.Ordinal))
        {
            return;
        }

        bool changed;
        bool reload;
        int epoch;
        lock (workspace.Gate)
        {
            if (pane.Listing is not { } listing)
            {
                // A navigation has not published its rows yet: its publish applies what follows from its page.
                if (pane.IsNavigating && pane.PendingListingDeltas.Count < MaxPendingDeltas)
                {
                    pane.PendingListingDeltas.Add(delta);
                }

                return;
            }

            changed = ApplyDeltaLocked(pane, delta);
            // A fill that is running notices the flag itself.
            reload = listing.NeedsReload && !pane.ListingInProgress;
            epoch = pane.ListingEpoch;
            if (changed && !pane.ListingInProgress)
            {
            }
        }

        if (changed)
        {
            workspace.NotifyChanged();
            workspace.StartDecorationRefresh(paneId);
        }

        if (reload)
        {
            _ = Task.Run(() => ReloadAsync(workspace, paneId, epoch));
        }
    }

    /// <summary>
    /// Asks the service to enumerate the pane's folder again and applies what changed. Returns false when the pane
    /// has no view (or just lost it): the caller then lists the folder whole.
    /// </summary>
    public async Task<bool> TryRefreshAsync(ExplorerWorkspace workspace, PaneId paneId, CancellationToken cancellationToken)
    {
        var pane = workspace.Pane(paneId);
        if (workspace.Backend.ListingViews is not { } views)
        {
            return false;
        }

        string viewId;
        int epoch;
        lock (workspace.Gate)
        {
            if (pane.Listing is not { } listing || pane.IsNavigating)
            {
                return false;
            }

            viewId = listing.ViewId;
            epoch = pane.ListingEpoch;
        }

        ListingViewDelta delta;
        try
        {
            delta = await views.RefreshListingViewAsync(viewId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The view is gone (the service restarted): forget it and let the caller list the folder whole.
            lock (workspace.Gate)
            {
                if (pane.Listing is not null && pane.ListingEpoch == epoch)
                {
                    pane.Listing = null;
                    pane.ListingEpoch++;
                    pane.PendingListingDeltas.Clear();
                    pane.ListingInProgress = false;
                }
            }

            return false;
        }

        OnDelta(workspace, delta);
        return true;
    }

    /// <summary>
    /// Under the gate: applies one delta to the pane's rows. Returns true when the rows changed. A delta that does
    /// not follow from the revision the pane holds marks the view for a reload instead; one the pane already
    /// reflects (a refresh's reply and the watcher's event can report the same change) does nothing.
    /// </summary>
    private static bool ApplyDeltaLocked(ExplorerPane pane, ListingViewDelta delta)
    {
        if (pane.Listing is not { } listing || delta.Revision <= listing.Revision)
        {
            return false;
        }

        if (delta.Reset || delta.BaseRevision != listing.Revision)
        {
            listing.NeedsReload = true;
            return false;
        }

        NamedPipeJsonClient.NormalizeListingViewDelta(listing.Path, delta);
        var complete = pane.Entries.Count >= listing.Total;
        var renames = new List<PathRename>();
        pane.Entries = PaneListing.Apply(
            pane.Entries, delta, pane.SelectedPath, out var selectionRemoved, out var selectionRenamedTo, renames);
        if (selectionRenamedTo is not null)
        {
            pane.SelectedPath = selectionRenamedTo;
        }
        else if (selectionRemoved)
        {
            pane.SelectedPath = null;
        }

        if (renames.Count > 0)
        {
            // Kept for the window, which holds its selected rows by path. Bounded: a window that never asks (a
            // test, a pane that is not shown) must not grow this for ever.
            pane.RenamedPaths.AddRange(renames);
            if (pane.RenamedPaths.Count > MaxRenamedPaths)
            {
                pane.RenamedPaths.RemoveRange(0, pane.RenamedPaths.Count - MaxRenamedPaths);
            }
        }

        listing.Revision = delta.Revision;
        // Complete rows stay complete: the view has exactly the rows the pane holds.
        listing.Total = complete ? pane.Entries.Count : Math.Max(ClampToInt(delta.Total), pane.Entries.Count);
        pane.BumpContentVersion();
        return true;
    }

    private static int ClampToInt(uint value) => (int)Math.Min(value, int.MaxValue);
}
