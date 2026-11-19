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

    public string LazyColumnModeFor(string kind) => LazyColumns.ModeFor(Settings.LazyColumnModes, kind);

    public void SetLazyColumnMode(string kind, string mode)
    {
        var modes = new Dictionary<string, string>(Settings.LazyColumnModes, StringComparer.Ordinal) { [kind] = mode };
        Settings.LazyColumnModes = LazyColumns.NormalizeModes(modes);
        NotifyChanged();
    }
}
