using System.Text.Json.Serialization;

namespace SimpleFile.Core;

public sealed class WorkspaceLayout
{
    public const string SettingsKey = "workspace-layout";

    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    [JsonPropertyName("dualPaneEnabled")]
    public bool DualPaneEnabled { get; set; }

    [JsonPropertyName("activePane")]
    public PaneId ActivePane { get; set; } = PaneId.Primary;

    [JsonPropertyName("sortBy")]
    public string SortBy { get; set; } = "name";

    [JsonPropertyName("sortAscending")]
    public bool SortAscending { get; set; } = true;

    [JsonPropertyName("primary")]
    public WorkspacePaneLayout Primary { get; set; } = new();

    [JsonPropertyName("secondary")]
    public WorkspacePaneLayout Secondary { get; set; } = new();
}

public sealed class WorkspacePaneLayout
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("activeTabId")]
    public string? ActiveTabId { get; set; }

    [JsonPropertyName("tabs")]
    public List<WorkspaceTabLayout> Tabs { get; set; } = [];

    [JsonPropertyName("view")]
    public string View { get; set; } = "";

    [JsonPropertyName("iconSize")]
    public int? IconSize { get; set; }

    [JsonPropertyName("sortBy")]
    public string SortBy { get; set; } = "";

    [JsonPropertyName("sortAscending")]
    public bool? SortAscending { get; set; }
}

public sealed class WorkspaceTabLayout
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("history")]
    public List<string> History { get; set; } = [];

    [JsonPropertyName("historyIndex")]
    public int HistoryIndex { get; set; } = -1;
}
