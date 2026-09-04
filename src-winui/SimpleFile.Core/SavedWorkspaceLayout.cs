using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleFile.Core;

public sealed class SavedWorkspaceLayoutsDocument
{
    public const string SettingsKey = "workspace-layouts";
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("layouts")]
    public List<SavedWorkspaceLayout> Layouts { get; set; } = [];

    public static SavedWorkspaceLayoutsDocument FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SavedWorkspaceLayoutsDocument();
        }

        try
        {
            var document = JsonSerializer.Deserialize<SavedWorkspaceLayoutsDocument>(json);
            document ??= new SavedWorkspaceLayoutsDocument();
            document.Normalize();
            return document;
        }
        catch
        {
            return new SavedWorkspaceLayoutsDocument();
        }
    }

    public string ToJson()
    {
        Normalize();
        return JsonSerializer.Serialize(this);
    }

    public SavedWorkspaceLayout? FindById(string? id)
    {
        var normalizedId = (id ?? "").Trim();
        return Layouts.FirstOrDefault(layout =>
            string.Equals(layout.Id, normalizedId, StringComparison.OrdinalIgnoreCase));
    }

    public SavedWorkspaceLayout? FindByName(string? name)
    {
        var normalizedName = NormalizeName(name);
        return Layouts.FirstOrDefault(layout =>
            string.Equals(layout.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeName(string? name)
    {
        var collapsed = string.Join(
            " ",
            (name ?? "").Trim().Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 80 ? collapsed : collapsed[..80].TrimEnd();
    }

    public static string NewId() => Guid.NewGuid().ToString("N");

    private void Normalize()
    {
        Version = CurrentVersion;
        var cleaned = new List<SavedWorkspaceLayout>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var layout in Layouts)
        {
            var name = NormalizeName(layout.Name);
            if (string.IsNullOrWhiteSpace(name) || !seenNames.Add(name))
            {
                continue;
            }

            var id = (layout.Id ?? "").Trim();
            if (string.IsNullOrWhiteSpace(id) || !seenIds.Add(id))
            {
                id = NewId();
                seenIds.Add(id);
            }

            var createdAt = layout.CreatedAt == default ? DateTimeOffset.UtcNow : layout.CreatedAt;
            var updatedAt = layout.UpdatedAt == default ? createdAt : layout.UpdatedAt;
            layout.Chrome ??= new WorkspaceChromeLayout();
            layout.Chrome.Normalize();

            cleaned.Add(new SavedWorkspaceLayout
            {
                Id = id,
                Name = name,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
                Layout = layout.Layout ?? new WorkspaceLayout(),
                Chrome = layout.Chrome,
            });
        }

        Layouts = cleaned;
    }
}

public sealed class SavedWorkspaceLayout
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("layout")]
    public WorkspaceLayout Layout { get; set; } = new();

    [JsonPropertyName("chrome")]
    public WorkspaceChromeLayout? Chrome { get; set; } = new();
}

public sealed class WorkspaceChromeLayout
{
    [JsonPropertyName("keepFoldersOnTop")]
    public bool KeepFoldersOnTop { get; set; } = true;

    [JsonPropertyName("enableGitIntegration")]
    public bool EnableGitIntegration { get; set; } = true;

    [JsonPropertyName("progressQueueVisible")]
    public bool ProgressQueueVisible { get; set; }

    [JsonPropertyName("previewVisible")]
    public bool PreviewVisible { get; set; } = true;

    [JsonPropertyName("previewWidth")]
    public double PreviewWidth { get; set; } = UiSettings.PreviewDefaultWidth;

    [JsonPropertyName("sidebarVisible")]
    public bool SidebarVisible { get; set; } = true;

    [JsonPropertyName("sidebarWidth")]
    public double SidebarWidth { get; set; } = UiSettings.SidebarDefaultWidth;

    [JsonPropertyName("dualPanePrimaryPercent")]
    public double DualPanePrimaryPercent { get; set; } = UiSettings.DualPaneDefaultPercent;

    [JsonPropertyName("dualPanePrimaryWidth")]
    public double DualPanePrimaryWidth { get; set; }

    [JsonPropertyName("quickAccessCollapsed")]
    public bool QuickAccessCollapsed { get; set; }

    [JsonPropertyName("myPcCollapsed")]
    public bool MyPcCollapsed { get; set; }

    [JsonPropertyName("columnPreset")]
    public string ColumnPreset { get; set; } = "default";

    [JsonPropertyName("visibleColumnIds")]
    public List<string> VisibleColumnIds { get; set; } = [];

    [JsonPropertyName("columnWidths")]
    public Dictionary<string, double> ColumnWidths { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("secondaryColumnPreset")]
    public string? SecondaryColumnPreset { get; set; }

    [JsonPropertyName("secondaryVisibleColumnIds")]
    public List<string>? SecondaryVisibleColumnIds { get; set; }

    [JsonPropertyName("secondaryColumnWidths")]
    public Dictionary<string, double>? SecondaryColumnWidths { get; set; }

    public static WorkspaceChromeLayout Capture(
        UiSettings settings,
        ColumnLayout primaryColumns,
        ColumnLayout secondaryColumns)
    {
        return new WorkspaceChromeLayout
        {
            KeepFoldersOnTop = settings.KeepFoldersOnTop,
            EnableGitIntegration = settings.EnableGitIntegration,
            ProgressQueueVisible = settings.ProgressQueueVisible,
            PreviewVisible = settings.PreviewVisible,
            PreviewWidth = UiSettings.NormalizePreviewWidth(settings.PreviewWidth),
            SidebarVisible = settings.SidebarVisible,
            SidebarWidth = UiSettings.NormalizeSidebarWidth(settings.SidebarWidth),
            DualPanePrimaryPercent = UiSettings.NormalizeDualPanePrimaryPercent(settings.DualPanePrimaryPercent),
            DualPanePrimaryWidth = UiSettings.NormalizeDualPanePrimaryWidth(settings.DualPanePrimaryWidth),
            QuickAccessCollapsed = settings.QuickAccessCollapsed,
            MyPcCollapsed = settings.MyPcCollapsed,
            ColumnPreset = UiSettings.NormalizeColumnPreset(settings.ColumnPreset),
            VisibleColumnIds = primaryColumns.SnapshotVisibleIds(),
            ColumnWidths = primaryColumns.SnapshotWidths(),
            SecondaryColumnPreset = UiSettings.NormalizeColumnPreset(settings.SecondaryColumnPreset),
            SecondaryVisibleColumnIds = secondaryColumns.SnapshotVisibleIds(),
            SecondaryColumnWidths = secondaryColumns.SnapshotWidths(),
        };
    }

    public void Apply(UiSettings settings, ColumnLayout primaryColumns, ColumnLayout secondaryColumns)
    {
        Normalize();
        settings.KeepFoldersOnTop = KeepFoldersOnTop;
        settings.EnableGitIntegration = EnableGitIntegration;
        settings.ProgressQueueVisible = ProgressQueueVisible;
        settings.PreviewVisible = PreviewVisible;
        settings.PreviewWidth = PreviewWidth;
        settings.SidebarVisible = SidebarVisible;
        settings.SidebarWidth = SidebarWidth;
        settings.DualPanePrimaryPercent = DualPanePrimaryPercent;
        settings.DualPanePrimaryWidth = DualPanePrimaryWidth;
        settings.QuickAccessCollapsed = QuickAccessCollapsed;
        settings.MyPcCollapsed = MyPcCollapsed;

        settings.ColumnPreset = ColumnPreset;
        settings.ColumnWidths = new Dictionary<string, double>(ColumnWidths, StringComparer.Ordinal);
        primaryColumns.ApplyPreset(settings.ColumnPreset);
        primaryColumns.RestoreVisibleIds(VisibleColumnIds);
        primaryColumns.RestoreWidths(settings.ColumnWidths);

        // Legacy chrome layouts without secondary* fields seed both panes from the flat primary fields.
        var secondaryPreset = UiSettings.NormalizeColumnPreset(
            string.IsNullOrWhiteSpace(SecondaryColumnPreset) ? ColumnPreset : SecondaryColumnPreset);
        var secondaryVisible = SecondaryVisibleColumnIds is { Count: > 0 }
            ? SecondaryVisibleColumnIds
            : VisibleColumnIds;
        var secondaryWidths = SecondaryColumnWidths is { Count: > 0 }
            ? SecondaryColumnWidths
            : ColumnWidths;
        settings.SecondaryColumnPreset = secondaryPreset;
        settings.SecondaryColumnWidths = new Dictionary<string, double>(secondaryWidths, StringComparer.Ordinal);
        secondaryColumns.ApplyPreset(secondaryPreset);
        secondaryColumns.RestoreVisibleIds(secondaryVisible);
        secondaryColumns.RestoreWidths(settings.SecondaryColumnWidths);
    }

    public void Normalize()
    {
        PreviewWidth = UiSettings.NormalizePreviewWidth(PreviewWidth);
        SidebarWidth = UiSettings.NormalizeSidebarWidth(SidebarWidth);
        DualPanePrimaryPercent = UiSettings.NormalizeDualPanePrimaryPercent(DualPanePrimaryPercent);
        DualPanePrimaryWidth = UiSettings.NormalizeDualPanePrimaryWidth(DualPanePrimaryWidth);
        ColumnPreset = UiSettings.NormalizeColumnPreset(ColumnPreset);
        if (!string.IsNullOrWhiteSpace(SecondaryColumnPreset))
        {
            SecondaryColumnPreset = UiSettings.NormalizeColumnPreset(SecondaryColumnPreset);
        }

        var knownColumns = new ColumnLayout();
        VisibleColumnIds = NormalizeVisibleIds(VisibleColumnIds, knownColumns);
        SecondaryVisibleColumnIds = SecondaryVisibleColumnIds is null
            ? null
            : NormalizeVisibleIds(SecondaryVisibleColumnIds, knownColumns);
        ColumnWidths = NormalizeWidths(ColumnWidths);
        SecondaryColumnWidths = SecondaryColumnWidths is null
            ? null
            : NormalizeWidths(SecondaryColumnWidths);
    }

    private static List<string> NormalizeVisibleIds(IEnumerable<string> ids, ColumnLayout knownColumns) =>
        ids
            .Where(id => !string.IsNullOrWhiteSpace(id) && knownColumns.Find(id) is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static Dictionary<string, double> NormalizeWidths(Dictionary<string, double> widths) =>
        widths
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                && !double.IsNaN(pair.Value)
                && !double.IsInfinity(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}
