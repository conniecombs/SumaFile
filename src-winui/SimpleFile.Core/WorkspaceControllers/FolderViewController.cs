using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Core;

internal sealed class FolderViewController
{
    public void ApplyUiSettings(
        ExplorerWorkspace workspace,
        UiSettings settings,
        bool applyViewDefaultsToPanes = true)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(settings);

        workspace.SetSettingsCore(settings);
        settings.Theme = UiSettings.NormalizeTheme(settings.Theme);
        settings.DefaultView = UiSettings.NormalizeDefaultView(settings.DefaultView);
        settings.DefaultIconSize = UiSettings.NormalizeIconSize(settings.DefaultIconSize);
        settings.SidebarWidth = UiSettings.NormalizeSidebarWidth(settings.SidebarWidth);
        settings.PreviewWidth = UiSettings.NormalizePreviewWidth(settings.PreviewWidth);
        settings.DualPanePrimaryPercent = UiSettings.NormalizeDualPanePrimaryPercent(settings.DualPanePrimaryPercent);
        settings.DualPanePrimaryWidth = UiSettings.NormalizeDualPanePrimaryWidth(settings.DualPanePrimaryWidth);
        settings.ShortcutOverrides = KeyboardShortcutMap.NormalizeOverrides(settings.ShortcutOverrides);
        settings.CommandSurface.Normalize();
        settings.FolderViewSettings.Normalize();
        workspace.SetShowHiddenFilesCore(settings.ShowHidden);
        if (applyViewDefaultsToPanes)
        {
            ApplyDefaultViewOptions(workspace.Primary, workspace.Secondary, settings);
        }

        ApplyColumnSettings(settings, workspace.PrimaryColumns, workspace.SecondaryColumns);
        workspace.NotifyChanged();
    }

    public void SetShowHidden(ExplorerWorkspace workspace, bool showHidden)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        workspace.SetShowHiddenFilesCore(showHidden);
        workspace.Settings.ShowHidden = showHidden;
        workspace.NotifyChanged();
    }

    public bool ToggleShowHidden(ExplorerWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        SetShowHidden(workspace, !workspace.ShowHiddenFiles);
        return workspace.ShowHiddenFiles;
    }

    public void SetFileListView(ExplorerWorkspace workspace, PaneId pane, string view)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var target = workspace.Pane(pane);
        var next = UiSettings.NormalizeDefaultView(view);
        if (string.Equals(target.View, next, StringComparison.Ordinal))
        {
            return;
        }

        target.View = next;
        workspace.NotifyChanged();
    }

    public void SetFileListIconSize(ExplorerWorkspace workspace, PaneId pane, int iconSize)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var target = workspace.Pane(pane);
        var next = UiSettings.NormalizeIconSize(iconSize);
        if (target.IconSize == next)
        {
            return;
        }

        target.IconSize = next;
        workspace.NotifyChanged();
    }

    public int NudgeFileListIconSize(ExplorerWorkspace workspace, PaneId pane, int steps)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var target = workspace.Pane(pane);
        var next = UiSettings.NormalizeIconSize(target.IconSize + (steps * UiSettings.IconSizeStep));
        SetFileListIconSize(workspace, pane, next);
        return target.IconSize;
    }

    public void ApplyViewOptionsToBothPanes(ExplorerWorkspace workspace, PaneId sourcePane)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var source = workspace.Pane(sourcePane);
        var primaryChanged = !SameViewOptions(workspace.Primary, source);
        var secondaryChanged = !SameViewOptions(workspace.Secondary, source);
        workspace.Primary.CopyViewOptionsFrom(source);
        workspace.Secondary.CopyViewOptionsFrom(source);
        if (primaryChanged || secondaryChanged)
        {
            workspace.NotifyChanged();
        }
    }

    public void SetSort(ExplorerWorkspace workspace, PaneId pane, string sortBy)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var target = workspace.Pane(pane);
        var next = string.IsNullOrWhiteSpace(sortBy) ? "name" : sortBy;
        var currentSort = target.SortBy;
        var currentAscending = target.SortAscending;
        target.SetSort(next);
        if (!string.Equals(target.SortBy, currentSort, StringComparison.OrdinalIgnoreCase)
            || target.SortAscending != currentAscending)
        {
            workspace.NotifyChanged();
        }
    }

    public FolderViewRule? EffectiveFolderViewRuleFor(ExplorerWorkspace workspace, PaneId pane)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        lock (workspace.Gate)
        {
            return ResolveRule(workspace.Settings, workspace.Pane(pane).Path, workspace.Drives);
        }
    }

    public async Task<FolderViewRule> SaveFolderViewSettingsAsync(
        ExplorerWorkspace workspace,
        FolderViewScope scope,
        PaneId? pane = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var target = workspace.Normalize(pane ?? workspace.ActivePane);
        FolderViewRule saved;
        lock (workspace.Gate)
        {
            var state = workspace.Pane(target);
            saved = UpsertRule(
                workspace.Settings,
                scope,
                state,
                workspace.ColumnsFor(state.Id),
                workspace.Drives,
                workspace.ShowHiddenFiles,
                workspace.ActiveProfileId);
        }

        await SaveUiSettingsAsync(workspace, cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public bool ApplyFolderViewSettingsForPathLocked(ExplorerWorkspace workspace, PaneId pane, string path)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var showHidden = workspace.ShowHiddenFiles;
        var activeProfileId = workspace.ActiveProfileId;
        var changed = ApplyForPath(
            workspace.Pane(pane),
            workspace.ColumnsFor(pane),
            path,
            workspace.Settings,
            workspace.Drives,
            ref showHidden,
            ref activeProfileId);
        workspace.SetShowHiddenFilesCore(showHidden);
        workspace.SetActiveProfileIdCore(activeProfileId);
        return changed;
    }

    public async Task LoadUiSettingsAsync(
        ExplorerWorkspace workspace,
        PlacesController places,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(places);
        if (workspace.FileOps is null)
        {
            return;
        }

        try
        {
            var state = await WorkspaceSettingsStore.LoadAsync(workspace.FileOps, cancellationToken).ConfigureAwait(false);
            workspace.SetSettingsCore(state.Settings);
            places.ApplyLoaded(state.Bookmarks, state.RecentPaths);
            workspace.Settings.FolderViewSettings.Normalize();
            workspace.SetShowHiddenFilesCore(workspace.Settings.ShowHidden);
            ApplyDefaultViewOptions(workspace.Primary, workspace.Secondary, workspace.Settings);
            ApplyColumnSettings(workspace.Settings, workspace.PrimaryColumns, workspace.SecondaryColumns);
        }
        catch
        {
            // Missing keys or a stub IPC keep defaults.
        }
    }

    public async Task SaveUiSettingsAsync(
        ExplorerWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.FileOps is null)
        {
            return;
        }

        workspace.Settings.ColumnPreset = UiSettings.NormalizeColumnPreset(workspace.Settings.ColumnPreset);
        workspace.Settings.SecondaryColumnPreset = UiSettings.NormalizeColumnPreset(workspace.Settings.SecondaryColumnPreset);
        await WorkspaceSettingsStore.SaveAsync(
            workspace.FileOps,
            workspace.Settings,
            workspace.PrimaryColumns,
            workspace.SecondaryColumns,
            workspace.ShowHiddenFiles,
            workspace.Bookmarks,
            workspace.RecentPaths,
            cancellationToken).ConfigureAwait(false);
    }

    public void ApplyDefaultViewOptions(ExplorerPane primary, ExplorerPane secondary, UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);
        ArgumentNullException.ThrowIfNull(settings);
        primary.ApplyViewDefaults(settings);
        secondary.ApplyViewDefaults(settings);
    }

    public void ApplyColumnSettings(
        UiSettings settings,
        ColumnLayout primaryColumns,
        ColumnLayout secondaryColumns)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(primaryColumns);
        ArgumentNullException.ThrowIfNull(secondaryColumns);
        primaryColumns.ApplyPreset(
            string.IsNullOrWhiteSpace(settings.ColumnPreset) ? "default" : settings.ColumnPreset);
        primaryColumns.RestoreWidths(settings.ColumnWidths);
        secondaryColumns.ApplyPreset(
            string.IsNullOrWhiteSpace(settings.SecondaryColumnPreset) ? "default" : settings.SecondaryColumnPreset);
        secondaryColumns.RestoreWidths(settings.SecondaryColumnWidths);
    }

    public static bool SameViewOptions(ExplorerPane left, ExplorerPane right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return string.Equals(left.View, right.View, StringComparison.Ordinal)
            && left.IconSize == right.IconSize
            && string.Equals(left.SortBy, right.SortBy, StringComparison.OrdinalIgnoreCase)
            && left.SortAscending == right.SortAscending;
    }

    public FolderViewRule? ResolveRule(
        UiSettings settings,
        string path,
        IReadOnlyList<DriveInfo> drives)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.FolderViewSettings.Resolve(path, drives);
    }

    public FolderViewOptions CaptureOptions(
        ExplorerPane pane,
        ColumnLayout columns,
        UiSettings settings,
        bool showHiddenFiles,
        string activeProfileId)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(settings);
        var preset = pane.Id == PaneId.Secondary ? settings.SecondaryColumnPreset : settings.ColumnPreset;
        return new FolderViewOptions
        {
            View = pane.View,
            IconSize = pane.IconSize,
            VisibleColumnIds = columns.SnapshotVisibleIds(),
            ColumnWidths = columns.SnapshotWidths(),
            SortBy = pane.SortBy,
            SortAscending = pane.SortAscending,
            ShowHidden = showHiddenFiles,
            WorkspaceProfileId = activeProfileId,
            ColumnPreset = preset,
        };
    }

    public FolderViewRule UpsertRule(
        UiSettings settings,
        FolderViewScope scope,
        ExplorerPane pane,
        ColumnLayout columns,
        IReadOnlyList<DriveInfo> drives,
        bool showHiddenFiles,
        string activeProfileId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.FolderViewSettings.Upsert(
            scope,
            pane.Path,
            drives,
            CaptureOptions(pane, columns, settings, showHiddenFiles, activeProfileId));
    }

    public bool ApplyForPath(
        ExplorerPane pane,
        ColumnLayout columns,
        string path,
        UiSettings settings,
        IReadOnlyList<DriveInfo> drives,
        ref bool showHiddenFiles,
        ref string activeProfileId)
    {
        var rule = ResolveRule(settings, path, drives);
        return rule is not null
            && ApplyOptions(pane, columns, rule.Options, settings, ref showHiddenFiles, ref activeProfileId);
    }

    public bool ApplyOptions(
        ExplorerPane pane,
        ColumnLayout columns,
        FolderViewOptions options,
        UiSettings settings,
        ref bool showHiddenFiles,
        ref string activeProfileId)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);

        options.Normalize();
        var changed = false;
        if (!string.IsNullOrWhiteSpace(options.View))
        {
            var view = UiSettings.NormalizeDefaultView(options.View);
            if (!string.Equals(pane.View, view, StringComparison.Ordinal))
            {
                pane.View = view;
                changed = true;
            }
        }

        if (options.IconSize is { } iconSize)
        {
            var normalizedIconSize = UiSettings.NormalizeIconSize(iconSize);
            if (pane.IconSize != normalizedIconSize)
            {
                pane.IconSize = normalizedIconSize;
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.SortBy) || options.SortAscending.HasValue)
        {
            var sortBy = string.IsNullOrWhiteSpace(options.SortBy) ? pane.SortBy : options.SortBy.Trim();
            var sortAscending = options.SortAscending ?? pane.SortAscending;
            if (!string.Equals(pane.SortBy, sortBy, StringComparison.OrdinalIgnoreCase)
                || pane.SortAscending != sortAscending)
            {
                pane.SortBy = sortBy;
                pane.SortAscending = sortAscending;
                changed = true;
            }
        }

        // Folder-view column options apply to the pane being viewed, not a shared layout.
        if (!string.IsNullOrWhiteSpace(options.ColumnPreset))
        {
            var preset = UiSettings.NormalizeColumnPreset(options.ColumnPreset);
            if (pane.Id == PaneId.Secondary)
            {
                if (!string.Equals(settings.SecondaryColumnPreset, preset, StringComparison.Ordinal))
                {
                    settings.SecondaryColumnPreset = preset;
                    changed = true;
                }
            }
            else if (!string.Equals(settings.ColumnPreset, preset, StringComparison.Ordinal))
            {
                settings.ColumnPreset = preset;
                changed = true;
            }

            columns.ApplyPreset(preset);
            changed = true;
        }

        if (options.VisibleColumnIds is { Count: > 0 } visibleColumnIds)
        {
            columns.RestoreVisibleIds(visibleColumnIds);
            changed = true;
        }

        if (options.ColumnWidths is { Count: > 0 } columnWidths)
        {
            var widths = new Dictionary<string, double>(columnWidths, StringComparer.Ordinal);
            if (pane.Id == PaneId.Secondary)
            {
                settings.SecondaryColumnWidths = widths;
            }
            else
            {
                settings.ColumnWidths = widths;
            }

            columns.RestoreWidths(widths);
            changed = true;
        }

        if (options.ShowHidden.HasValue && showHiddenFiles != options.ShowHidden.Value)
        {
            showHiddenFiles = options.ShowHidden.Value;
            settings.ShowHidden = options.ShowHidden.Value;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(options.WorkspaceProfileId)
            && !string.Equals(activeProfileId, options.WorkspaceProfileId, StringComparison.OrdinalIgnoreCase))
        {
            activeProfileId = options.WorkspaceProfileId.Trim();
            changed = true;
        }

        return changed;
    }
}
