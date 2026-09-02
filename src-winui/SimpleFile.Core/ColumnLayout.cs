namespace SimpleFile.Core;

public sealed class FileListColumn
{
    public FileListColumn(string id, string label, string sort, double width, double minWidth, double maxWidth)
    {
        Id = id;
        Label = label;
        Sort = sort;
        Width = width;
        MinWidth = minWidth;
        MaxWidth = maxWidth;
    }

    public string Id { get; }
    public string Label { get; }
    public string Sort { get; }
    public double Width { get; set; }
    public double MinWidth { get; }
    public double MaxWidth { get; }
}

/// <summary>
/// Default widths/presets match frontend/src/lib/fileListColumns.ts.
/// </summary>
public sealed class ColumnLayout
{
    public const double UnboundedMaxWidth = 10000;

    public static readonly string[] DefaultVisible = ["name", "size", "date", "type"];

    public static readonly IReadOnlyDictionary<string, string[]> Presets = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["default"] = ["name", "size", "date", "type"],
        ["details"] = ["name", "size", "items", "date", "type", "extension"],
        ["media"] = ["name", "size", "date", "extension", "type"],
        ["developer"] = ["name", "size", "date", "extension", "git", "symlink", "path"],
        ["photo"] = ["name", "date", "size", "extension", "type"],
    };

    public ColumnLayout()
    {
        Columns =
        [
            new("name", "Name", "name", 240, 80, UnboundedMaxWidth),
            new("size", "Size", "size", 100, 36, UnboundedMaxWidth),
            new("items", "Items", "items", 86, 36, UnboundedMaxWidth),
            new("date", "Modified", "date", 160, 36, UnboundedMaxWidth),
            new("type", "Type", "type", 100, 36, UnboundedMaxWidth),
            new("extension", "Ext", "extension", 72, 36, UnboundedMaxWidth),
            new("git", "Git", "git", 92, 36, UnboundedMaxWidth),
            new("symlink", "Link target", "symlink", 180, 36, UnboundedMaxWidth),
            new("path", "Path", "path", 220, 36, UnboundedMaxWidth),
            new("parent", "Parent", "parent", 180, 36, UnboundedMaxWidth),
        ];
        VisibleIds = [.. DefaultVisible];
    }

    public event EventHandler? Changed;

    public List<FileListColumn> Columns { get; }

    public List<string> VisibleIds { get; }

    public IReadOnlyList<FileListColumn> VisibleColumns =>
        VisibleIds
            .Select(id => Columns.FirstOrDefault(column => column.Id == id))
            .Where(column => column is not null)
            .Cast<FileListColumn>()
            .ToList();

    public double VisibleWidth => VisibleColumns.Sum(column => column.Width);

    public bool IsVisible(string id)
    {
        return VisibleIds.Any(visible => string.Equals(visible, id, StringComparison.Ordinal));
    }

    public FileListColumn? Find(string id)
    {
        return Columns.FirstOrDefault(column => string.Equals(column.Id, id, StringComparison.Ordinal));
    }

    public double WidthOf(string id)
    {
        return Find(id)?.Width ?? 100;
    }

    public void Resize(string id, double width)
    {
        var column = Find(id);
        if (column is null)
        {
            return;
        }

        column.Width = Math.Clamp(width, column.MinWidth, column.MaxWidth);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyPreset(string preset)
    {
        if (!Presets.TryGetValue(preset, out var ids))
        {
            ids = DefaultVisible;
        }

        VisibleIds.Clear();
        VisibleIds.AddRange(ids.Where(id => Find(id) is not null));
        if (VisibleIds.Count == 0)
        {
            VisibleIds.AddRange(DefaultVisible);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public List<string> SnapshotVisibleIds()
    {
        return VisibleIds
            .Where(id => Find(id) is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public void RestoreVisibleIds(IReadOnlyList<string>? ids)
    {
        if (ids is null || ids.Count == 0)
        {
            return;
        }

        var visible = ids
            .Where(id => !string.IsNullOrWhiteSpace(id) && Find(id) is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (visible.Count == 0)
        {
            return;
        }

        VisibleIds.Clear();
        VisibleIds.AddRange(visible);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Dictionary<string, double> SnapshotWidths()
    {
        return Columns.ToDictionary(column => column.Id, column => column.Width, StringComparer.Ordinal);
    }

    public void RestoreWidths(IReadOnlyDictionary<string, double>? widths)
    {
        if (widths is null)
        {
            return;
        }

        foreach (var (id, width) in widths)
        {
            var column = Find(id);
            if (column is not null)
            {
                column.Width = Math.Clamp(width, column.MinWidth, column.MaxWidth);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
