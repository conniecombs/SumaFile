namespace SimpleFile.Core;

internal sealed class PaneEnrichmentController
{
    public const int MaxFolderMetricEntries = 32;

    public async Task<bool> ApplyGitStatusesAsync(
        ExplorerPane state,
        FileOperationService? fileOps,
        bool enableGitIntegration,
        CancellationToken cancellationToken = default)
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

        var path = state.Path;
        var navigationToken = state.NavigationToken;
        try
        {
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

        return changed;
    }

    public async Task<bool> FillFolderMetricsAsync(
        ExplorerPane state,
        FileOperationService? fileOps,
        bool includeSizes,
        bool includeItemCounts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (fileOps is null || (!includeSizes && !includeItemCounts) || state.PathIsNetwork)
        {
            return false;
        }

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
                if (includeSizes && includeItemCounts)
                {
                    var metrics = await fileOps.GetFolderMetricsAsync(entry.Path, cancellationToken).ConfigureAwait(false);
                    if (!IsCurrent())
                    {
                        return false;
                    }

                    entry.Size = metrics.Size;
                    entry.ItemCount = metrics.ItemCount;
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
                    changed = true;
                }
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
        if (await ApplyGitStatusesAsync(state, workspace.FileOps, workspace.Settings.EnableGitIntegration, cancellationToken)
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
        if (await FillFolderMetricsAsync(state, workspace.FileOps, includeSizes, includeItemCounts, cancellationToken)
                .ConfigureAwait(false))
        {
            workspace.NotifyChanged();
        }
    }
}
