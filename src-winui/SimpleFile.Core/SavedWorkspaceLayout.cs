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

    public static WorkspaceChromeLayout Capture(UiSettings settings, ColumnLayout columns)
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
            VisibleColumnIds = columns.SnapshotVisibleIds(),
            ColumnWidths = columns.SnapshotWidths(),
        };
    }

    public void Apply(UiSettings settings, ColumnLayout columns)
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
        columns.ApplyPreset(settings.ColumnPreset);
        columns.RestoreVisibleIds(VisibleColumnIds);
        columns.RestoreWidths(settings.ColumnWidths);
    }

    public void Normalize()
    {
        PreviewWidth = UiSettings.NormalizePreviewWidth(PreviewWidth);
        SidebarWidth = UiSettings.NormalizeSidebarWidth(SidebarWidth);
        DualPanePrimaryPercent = UiSettings.NormalizeDualPanePrimaryPercent(DualPanePrimaryPercent);
        DualPanePrimaryWidth = UiSettings.NormalizeDualPanePrimaryWidth(DualPanePrimaryWidth);
        ColumnPreset = UiSettings.NormalizeColumnPreset(ColumnPreset);
        var knownColumns = new ColumnLayout();
        VisibleColumnIds = VisibleColumnIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && knownColumns.Find(id) is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        ColumnWidths = ColumnWidths
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                && !double.IsNaN(pair.Value)
                && !double.IsInfinity(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }
}
