using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleFile.Core;

public sealed class WorkspaceProfilesDocument
{
    public const string SettingsKey = "workspace-profiles";
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("activeProfileId")]
    public string ActiveProfileId { get; set; } = "";

    [JsonPropertyName("profiles")]
    public List<WorkspaceProfile> Profiles { get; set; } = [];

    public static WorkspaceProfilesDocument FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new WorkspaceProfilesDocument();
        }

        try
        {
            var document = JsonSerializer.Deserialize<WorkspaceProfilesDocument>(json)
                ?? new WorkspaceProfilesDocument();
            document.Normalize();
            return document;
        }
        catch
        {
            return new WorkspaceProfilesDocument();
        }
    }

    public string ToJson()
    {
        Normalize();
        return JsonSerializer.Serialize(this);
    }

    public WorkspaceProfile? FindById(string? id)
    {
        var normalizedId = (id ?? "").Trim();
        return Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, normalizedId, StringComparison.OrdinalIgnoreCase));
    }

    public WorkspaceProfile? FindByName(string? name)
    {
        var normalizedName = NormalizeName(name);
        return Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeName(string? name) => SavedWorkspaceLayoutsDocument.NormalizeName(name);

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static WorkspaceProfilesDocument FromLegacyLayouts(SavedWorkspaceLayoutsDocument legacy)
    {
        var document = new WorkspaceProfilesDocument();
        foreach (var layout in legacy.Layouts)
        {
            document.Profiles.Add(new WorkspaceProfile
            {
                Id = string.IsNullOrWhiteSpace(layout.Id) ? NewId() : layout.Id,
                Name = layout.Name,
                CreatedAt = layout.CreatedAt,
                UpdatedAt = layout.UpdatedAt,
                Layout = layout.Layout,
                Chrome = layout.Chrome ?? new WorkspaceChromeLayout(),
            });
        }

        document.Normalize();
        return document;
    }

    private void Normalize()
    {
        Version = CurrentVersion;
        ActiveProfileId = (ActiveProfileId ?? "").Trim();

        var cleaned = new List<WorkspaceProfile>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in Profiles)
        {
            var name = NormalizeName(profile.Name);
            if (string.IsNullOrWhiteSpace(name) || !seenNames.Add(name))
            {
                continue;
            }

            var id = (profile.Id ?? "").Trim();
            if (string.IsNullOrWhiteSpace(id) || !seenIds.Add(id))
            {
                id = NewId();
                seenIds.Add(id);
            }

            var createdAt = profile.CreatedAt == default ? DateTimeOffset.UtcNow : profile.CreatedAt;
            var updatedAt = profile.UpdatedAt == default ? createdAt : profile.UpdatedAt;
            profile.Chrome ??= new WorkspaceChromeLayout();
            profile.Chrome.Normalize();

            cleaned.Add(new WorkspaceProfile
            {
                Id = id,
                Name = name,
                BuiltInId = "",
                SourceProfileId = (profile.SourceProfileId ?? "").Trim(),
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
                Layout = profile.Layout ?? new WorkspaceLayout(),
                Chrome = profile.Chrome,
            });
        }

        Profiles = cleaned;
        if (!string.IsNullOrWhiteSpace(ActiveProfileId)
            && Profiles.All(profile => !string.Equals(profile.Id, ActiveProfileId, StringComparison.OrdinalIgnoreCase))
            && !WorkspaceProfileTemplates.IsBuiltInId(ActiveProfileId))
        {
            ActiveProfileId = "";
        }
    }
}

public sealed class WorkspaceProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("builtInId")]
    public string BuiltInId { get; set; } = "";

    [JsonPropertyName("sourceProfileId")]
    public string SourceProfileId { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("layout")]
    public WorkspaceLayout Layout { get; set; } = new();

    [JsonPropertyName("chrome")]
    public WorkspaceChromeLayout? Chrome { get; set; } = new();

    [JsonIgnore]
    public bool IsBuiltIn => !string.IsNullOrWhiteSpace(BuiltInId);

    [JsonIgnore]
    public bool CanRename => !IsBuiltIn;

    [JsonIgnore]
    public bool CanOverwrite => !IsBuiltIn;

    [JsonIgnore]
    public bool CanDelete => !IsBuiltIn;

    [JsonIgnore]
    public bool CanReset => IsBuiltIn || !string.IsNullOrWhiteSpace(SourceProfileId);

    public WorkspaceProfile Clone()
    {
        return JsonSerializer.Deserialize<WorkspaceProfile>(JsonSerializer.Serialize(this))
            ?? new WorkspaceProfile();
    }

    public WorkspaceProfile CloneAsUser(string name)
    {
        var now = DateTimeOffset.UtcNow;
        var clone = Clone();
        clone.Id = WorkspaceProfilesDocument.NewId();
        clone.Name = WorkspaceProfilesDocument.NormalizeName(name);
        clone.SourceProfileId = IsBuiltIn ? Id : SourceProfileId;
        clone.BuiltInId = "";
        clone.CreatedAt = now;
        clone.UpdatedAt = now;
        return clone;
    }
}

public sealed class WorkspaceProfileExportDocument
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = "sumafile.workspace-profile";

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("exportedAt")]
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("profile")]
    public WorkspaceProfile Profile { get; set; } = new();

    public static string ToJson(WorkspaceProfile profile)
    {
        var export = new WorkspaceProfileExportDocument
        {
            Profile = profile.Clone(),
        };
        return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
    }
}

public static class WorkspaceProfileTemplates
{
    public const string StandardId = "builtin-standard";
    public const string DeveloperId = "builtin-developer";
    public const string PhotosId = "builtin-photos";
    public const string TransferId = "builtin-transfer";
    public const string MinimalId = "builtin-minimal";

    private static readonly string[] BuiltInIds =
    [
        StandardId,
        DeveloperId,
        PhotosId,
        TransferId,
        MinimalId,
    ];

    public static bool IsBuiltInId(string? id)
    {
        return BuiltInIds.Any(candidate =>
            string.Equals(candidate, (id ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<WorkspaceProfile> All(string homePath)
    {
        var path = homePath ?? "";
        var now = DateTimeOffset.UnixEpoch;
        return
        [
            Profile(
                StandardId,
                "Standard",
                Layout(path, dualPane: false, primaryView: "details", primaryIconSize: 32),
                Chrome(
                    columnPreset: "default",
                    sidebarVisible: true,
                    previewVisible: true,
                    enableGitIntegration: false),
                now),
            Profile(
                DeveloperId,
                "Developer",
                Layout(path, dualPane: false, primaryView: "details", primaryIconSize: 16, primarySortBy: "name"),
                Chrome(
                    columnPreset: "developer",
                    sidebarVisible: true,
                    previewVisible: true,
                    enableGitIntegration: true,
                    keepFoldersOnTop: true),
                now),
            Profile(
                PhotosId,
                "Photos",
                Layout(path, dualPane: false, primaryView: "tiles", primaryIconSize: 192, primarySortBy: "date", primarySortAscending: false),
                Chrome(
                    columnPreset: "photo",
                    sidebarVisible: true,
                    previewVisible: true,
                    previewWidth: 420,
                    enableGitIntegration: false),
                now),
            Profile(
                TransferId,
                "Transfer",
                Layout(path, dualPane: true, primaryView: "details", primaryIconSize: 32, secondaryView: "details", secondaryIconSize: 32),
                Chrome(
                    columnPreset: "details",
                    sidebarVisible: true,
                    previewVisible: false,
                    enableGitIntegration: false,
                    progressQueueVisible: true),
                now),
            Profile(
                MinimalId,
                "Minimal",
                Layout(path, dualPane: false, primaryView: "list", primaryIconSize: 16),
                Chrome(
                    columnPreset: "default",
                    sidebarVisible: false,
                    previewVisible: false,
                    enableGitIntegration: false),
                now),
        ];
    }

    public static WorkspaceProfile? Find(string? id, string homePath)
    {
        return All(homePath).FirstOrDefault(profile =>
            string.Equals(profile.Id, (id ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static WorkspaceProfile Profile(
        string id,
        string name,
        WorkspaceLayout layout,
        WorkspaceChromeLayout chrome,
        DateTimeOffset timestamp)
    {
        return new WorkspaceProfile
        {
            Id = id,
            BuiltInId = id,
            Name = name,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            Layout = layout,
            Chrome = chrome,
        };
    }

    private static WorkspaceLayout Layout(
        string path,
        bool dualPane,
        string primaryView,
        int primaryIconSize,
        string primarySortBy = "name",
        bool primarySortAscending = true,
        string? secondaryView = null,
        int? secondaryIconSize = null,
        string secondarySortBy = "name",
        bool secondarySortAscending = true)
    {
        return new WorkspaceLayout
        {
            DualPaneEnabled = dualPane,
            ActivePane = PaneId.Primary,
            SortBy = primarySortBy,
            SortAscending = primarySortAscending,
            Primary = Pane(path, "profile-primary-tab", primaryView, primaryIconSize, primarySortBy, primarySortAscending),
            Secondary = dualPane
                ? Pane(path, "profile-secondary-tab", secondaryView ?? primaryView, secondaryIconSize ?? primaryIconSize, secondarySortBy, secondarySortAscending)
                : new WorkspacePaneLayout(),
        };
    }

    private static WorkspacePaneLayout Pane(
        string path,
        string tabId,
        string view,
        int iconSize,
        string sortBy,
        bool sortAscending)
    {
        var title = string.IsNullOrWhiteSpace(path) ? "Home" : PathRules.Basename(path);
        return new WorkspacePaneLayout
        {
            Path = path,
            ActiveTabId = tabId,
            View = view,
            IconSize = iconSize,
            SortBy = sortBy,
            SortAscending = sortAscending,
            Tabs = string.IsNullOrWhiteSpace(path)
                ? []
                :
                [
                    new WorkspaceTabLayout
                    {
                        Id = tabId,
                        Path = path,
                        Title = title,
                        History = [path],
                        HistoryIndex = 0,
                    },
                ],
        };
    }

    private static WorkspaceChromeLayout Chrome(
        string columnPreset,
        bool sidebarVisible,
        bool previewVisible,
        bool enableGitIntegration,
        bool keepFoldersOnTop = true,
        bool progressQueueVisible = false,
        double previewWidth = UiSettings.PreviewDefaultWidth)
    {
        var columns = new ColumnLayout();
        columns.ApplyPreset(columnPreset);
        return new WorkspaceChromeLayout
        {
            KeepFoldersOnTop = keepFoldersOnTop,
            EnableGitIntegration = enableGitIntegration,
            ProgressQueueVisible = progressQueueVisible,
            PreviewVisible = previewVisible,
            PreviewWidth = previewWidth,
            SidebarVisible = sidebarVisible,
            SidebarWidth = UiSettings.SidebarDefaultWidth,
            DualPanePrimaryPercent = UiSettings.DualPaneDefaultPercent,
            ColumnPreset = columnPreset,
            VisibleColumnIds = columns.SnapshotVisibleIds(),
            ColumnWidths = columns.SnapshotWidths(),
        };
    }
}
