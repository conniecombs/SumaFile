using System.Text.Json;

namespace SimpleFile.PluginHost.Manifest;

public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    PluginPublisher Publisher,
    string SdkVersion,
    PluginUiSpec? Ui,
    PluginBackendSpec? Backend,
    PluginContributions Contributions,
    IReadOnlyList<PluginSettingSpec> Settings,
    IReadOnlyList<string> Permissions);

public sealed record PluginPublisher(string Name, string? Url, string? PublicKey);

public sealed record PluginUiSpec(string Assembly, string EntryType);

public sealed record PluginBackendSpec(
    string Executable,
    IReadOnlyList<string> Args,
    string ActivateOn,
    bool AcceptsMultipleClients);

public sealed record PluginContributions(
    IReadOnlyList<PluginCommandSpec> Commands,
    IReadOnlyList<PluginContextMenuSpec> ContextMenu,
    IReadOnlyList<PluginToolbarSpec> Toolbar,
    IReadOnlyList<PluginArchiveFormatSpec> ArchiveFormats);

public sealed record PluginCommandSpec(string Id, string Label, string Group, string? Shortcut);

public sealed record PluginContextMenuSpec(string Group, IReadOnlyList<PluginContextMenuItemSpec> Items);

public sealed record PluginContextMenuItemSpec(string Command, string Label, string When);

public sealed record PluginToolbarSpec(string Command, string Label, string Glyph, string Placement);

public sealed record PluginArchiveFormatSpec(
    string Extension,
    bool CanList,
    bool CanExtract,
    bool CanCreate,
    bool CanModify,
    string EngineName);

public sealed record PluginSettingSpec(
    string Key,
    string Type,
    string Label,
    string? Description,
    JsonElement? Default,
    string? Group,
    IReadOnlyList<string>? Options);

public sealed record ManifestReadResult(PluginManifest? Manifest, IReadOnlyList<string> Errors)
{
    public bool IsValid => Manifest is not null;
}
