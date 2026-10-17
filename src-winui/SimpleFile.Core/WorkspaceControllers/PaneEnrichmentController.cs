namespace SimpleFile.Core;

internal sealed class PaneEnrichmentController
{
    public const int MaxFolderMetricEntries = 32;

    public async Task<bool> ApplyGitStatusesAsync(
        ExplorerPane state,
        FileOperationService? fileOps,
        bool enableGitIntegration,
        CancellationToken cancellationToken = default,
        ForegroundTaskScheduler? scheduler = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!enableGitIntegration)
        {
            return ClearGitStatuses(state);
        }

        if (fileOps is null || state.PathIsNetwork)
        {
            return false;
        }

        var activeScheduler = scheduler ?? ForegroundTaskScheduler.Shared;
        var path = state.Path;
        var navigationToken = state.NavigationToken;
        try
        {
            await activeScheduler.YieldOrThrottleAsync(cancellationToken).ConfigureAwait(false);
            if (navigationToken != state.NavigationToken || !PathRules.PathsEqual(state.Path, path))
            {
                return false;
            }

            var statuses = await fileOps.GetGitFileStatusesAsync(path, cancellationToken).ConfigureAwait(false);
            var map = statuses.ToDictionary(
                entry => entry.Path,
                entry => entry.GitStatus ?? "",
                StringComparer.OrdinalIgnoreCase);
            if (navigationToken != state.NavigationToken || !PathRules.PathsEqual(state.Path, path))
            {
                return false;
            }

            foreach (var entry in state.Entries)
            {
                entry.GitStatus = map.TryGetValue(entry.Path, out var status) ? status : null;
            }

            state.BumpContentVersion();
            return true;
        }
        catch
        {
            // Git is optional.
            return false;
        }
    }

    public bool ClearGitStatuses(ExplorerPane primary, ExplorerPane secondary)
    {
        var primaryCleared = ClearGitStatuses(primary);
        var secondaryCleared = ClearGitStatuses(secondary);
        return primaryCleared || secondaryCleared;
    }

    public static bool ClearGitStatuses(ExplorerPane state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var changed = false;
        foreach (var entry in state.Entries)
        {
            if (entry.GitStatus is not null)
            {
                entry.GitStatus = null;
                changed = true;
            }
        }

        if (changed)
        {
            state.BumpContentVersion();
        }

        return changed;
    }

    public async Task<bool> FillFolderMetricsAsync(
        ExplorerPane state,
        FileOperationService? fileOps,
        bool includeSizes,
        bool includeItemCounts,
        CancellationToken cancellationToken = default,
        ForegroundTaskScheduler? scheduler = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (fileOps is null || (!includeSizes && !includeItemCounts) || state.PathIsNetwork)
        {
            return false;
        }

        var activeScheduler = scheduler ?? ForegroundTaskScheduler.Shared;
        var path = state.Path;
        var navigationToken = state.NavigationToken;
        var folders = state.Entries.Where(item => item.IsDir).Take(MaxFolderMetricEntries).ToList();
        var changed = false;
        bool IsCurrent() =>
            !cancellationToken.IsCancellationRequested
            && navigationToken == state.NavigationToken
            && PathRules.PathsEqual(state.Path, path);

        foreach (var entry in folders)
        {
            if (!IsCurrent())
            {
                return false;
            }

            try
            {
                await activeScheduler.YieldOrThrottleAsync(cancellationToken).ConfigureAwait(false);
                if (!IsCurrent())
                {
                    return false;
                }

                if (includeSizes && includeItemCounts)
                {
                    var metrics = await fileOps.GetFolderMetricsAsync(entry.Path, cancellationToken).ConfigureAwait(false);
                    if (!IsCurrent())
                    {
                        return false;
                    }

                    entry.Size = metrics.Size;
                    entry.ItemCount = metrics.ItemCount;
                    state.BumpContentVersion();
                    changed = true;
                    continue;
                }

                if (includeSizes)
                {
                    var size = await fileOps.CalculateFolderSizeAsync(entry.Path, cancellationToken).ConfigureAwait(false);
                    if (!IsCurrent())
                    {
                        return false;
                    }

                    entry.Size = size;
                    state.BumpContentVersion();
                    changed = true;
                }

                if (includeItemCounts)
                {
                    var itemCount = await fileOps.CountFolderItemsAsync(entry.Path, cancellationToken).ConfigureAwait(false);
                    if (!IsCurrent())
                    {
                        return false;
                    }

                    entry.ItemCount = itemCount;
                    state.BumpContentVersion();
                    changed = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch
            {
                // Skip folders that cannot be measured or counted.
            }
        }

        return changed && IsCurrent();
    }

    public async Task ApplyGitStatusesAsync(
        ExplorerWorkspace workspace,
        PaneId pane,
        CancellationToken cancellationToken = default)
    {
        var state = workspace.Pane(workspace.Normalize(pane));
        if (await ApplyGitStatusesAsync(
                state,
                workspace.FileOps,
                workspace.Settings.EnableGitIntegration,
                cancellationToken,
                workspace.ForegroundScheduler)
            .ConfigureAwait(false))
        {
            workspace.NotifyChanged();
        }
    }

    public void ClearGitStatuses(ExplorerWorkspace workspace)
    {
        if (ClearGitStatuses(workspace.Primary, workspace.Secondary))
        {
            workspace.NotifyChanged();
        }
    }

    public async Task FillFolderMetricsAsync(
        ExplorerWorkspace workspace,
        PaneId pane,
        bool includeSizes,
        bool includeItemCounts,
        CancellationToken cancellationToken = default)
    {
        var state = workspace.Pane(workspace.Normalize(pane));
        if (await FillFolderMetricsAsync(
                state,
                workspace.FileOps,
                includeSizes,
                includeItemCounts,
                cancellationToken,
                workspace.ForegroundScheduler)
            .ConfigureAwait(false))
        {
            workspace.NotifyChanged();
        }
    }
}
