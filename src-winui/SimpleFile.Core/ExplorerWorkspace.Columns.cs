using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed partial class ExplorerWorkspace
{
    /// <summary>Shows or hides one column of <paramref name="pane"/>; the name column always stays.</summary>
    public void SetColumnVisible(PaneId pane, string columnId, bool visible)
    {
        ColumnsFor(pane).SetVisible(columnId, visible);
        NotifyChanged();
    }

    /// <summary>Replaces <paramref name="pane"/>'s columns with a preset's and remembers the preset's name.</summary>
    public void ApplyColumnPreset(PaneId pane, string preset)
    {
        var normalized = UiSettings.NormalizeColumnPreset(preset);
        ColumnsFor(pane).ApplyPreset(normalized);
        if (Normalize(pane) == PaneId.Secondary)
        {
            Settings.SecondaryColumnPreset = normalized;
        }
        else
        {
            Settings.ColumnPreset = normalized;
        }

        NotifyChanged();
    }

    /// <summary>
    /// A lazy column's mode: the one set for it, else the default, where the "show folder sizes" setting makes
    /// folder size automatic as it did before lazy columns.
    /// </summary>
    public string LazyColumnModeFor(string kind)
    {
        if (kind == LazyColumns.FolderSize && Settings.ShowFolderSizes && !Settings.LazyColumnModes.ContainsKey(kind))
        {
            return LazyColumns.Automatic;
        }

        return LazyColumns.ModeFor(Settings.LazyColumnModes, kind);
    }

    /// <summary>
    /// Reads a lazy fact the service cannot (a duration it marked unsupported) by other means: the app sets it to the
    /// Windows property system. Returns null when it found nothing either.
    /// </summary>
    public Func<string, string, CancellationToken, Task<EntryFact?>>? FactFallback { get; set; }

    /// <summary>The Details list shows rows <paramref name="first"/> to <paramref name="last"/>: their lazy columns are read.</summary>
    public void ReportViewport(PaneId pane, int first, int last) =>
        _lazyColumns.ReportViewport(this, pane, first, last);

    /// <summary>Reads one fact for one row now, whatever the column's mode (a click on a "…" cell).</summary>
    public void RequestFact(PaneId pane, string path, string kind) =>
        _lazyColumns.RequestFact(this, pane, path, kind);

    /// <summary>The latest fill of a lazy column for a whole folder (a sort by it, or "Calculate for this folder").</summary>
    public LazyColumnFillStatus? LazyColumnFill { get; private set; }

    internal void SetLazyColumnFillCore(LazyColumnFillStatus? value) => LazyColumnFill = value;

    /// <summary>Reads <paramref name="kind"/> for the whole folder of <paramref name="pane"/> in the background, with progress in <see cref="LazyColumnFill"/>.</summary>
    public void ReadColumnForFolder(PaneId pane, string kind) =>
        _lazyColumns.StartFill(this, pane, kind);

    /// <summary>Stops the fill under way for <paramref name="pane"/>; what it read stays.</summary>
    public void CancelColumnFill(PaneId pane) =>
        _lazyColumns.CancelFill(this, pane);

    /// <summary>
    /// A sort by a lazy column that reads as rows appear (Automatic mode) reads the whole folder first, so the order
    /// is right rather than by the few rows that happened to be on screen. An on-request column sorts by what is known.
    /// </summary>
    internal void StartLazySortFillIfNeeded(PaneId pane)
    {
        var id = Normalize(pane);
        _lazyColumns.CancelFill(this, id);
        var column = ColumnsFor(id).Find(Pane(id).SortBy);
        if (column?.FactKind is { } kind && LazyColumnModeFor(kind) == LazyColumns.Automatic)
        {
            _lazyColumns.StartFill(this, id, kind);
        }
    }

    /// <summary>Reads <paramref name="kind"/> for every row of the pane that can have it, for a sort by that column.</summary>
    public Task<LazyColumnFill> FillFolderFactsAsync(
        PaneId pane,
        string kind,
        IProgress<LazyColumnFill>? progress = null,
        CancellationToken cancellationToken = default) =>
        _lazyColumns.FillFolderFactsAsync(this, pane, kind, progress, cancellationToken);

    public void SetLazyColumnMode(string kind, string mode)
    {
        var modes = new Dictionary<string, string>(Settings.LazyColumnModes, StringComparer.Ordinal) { [kind] = mode };
        Settings.LazyColumnModes = LazyColumns.NormalizeModes(modes);
        NotifyChanged();
    }
}
