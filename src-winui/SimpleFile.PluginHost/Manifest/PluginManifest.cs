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
    IReadOnlyList<PluginArchiveFormatSpec> ArchiveFormats)
{
    /// <summary><c>contributions.sidebar</c>, in manifest order.</summary>
    public IReadOnlyList<PluginSidebarSpec> Sidebar { get; init; } = [];

    /// <summary><c>contributions.settingsPage</c>, or null when the plugin declares none.</summary>
    public PluginSettingsPageSpec? SettingsPage { get; init; }

    /// <summary><c>contributions.panels</c>, in manifest order.</summary>
    public IReadOnlyList<PluginPanelSpec> Panels { get; init; } = [];

    /// <summary><c>contributions.rowDecorations</c>, or null when the plugin declares none.</summary>
    public PluginRowDecorationSpec? RowDecorations { get; init; }
}

/// <summary>One <c>contributions.panels</c> entry.</summary>
/// <param name="Id">The panel id; follows the settings key grammar and is unique within the plugin.</param>
/// <param name="Title">The header text the host draws.</param>
/// <param name="Provider">Passed to <c>IPanelProvider.CreatePanel</c>.</param>
/// <param name="Placement"><c>bottom</c> (the default) or <c>right</c> of the file list.</param>
/// <param name="Detachable">Whether the host offers to move the panel into its own window.</param>
/// <param name="Command">A declared command that toggles the panel, or null; a command toggles at most one panel.</param>
public sealed record PluginPanelSpec(string Id, string Title, string Provider, string Placement, bool Detachable, string? Command);

/// <summary><c>contributions.rowDecorations</c>.</summary>
/// <param name="Provider">Passed to <c>IRowDecorationProvider.GetDecorationsAsync</c>.</param>
public sealed record PluginRowDecorationSpec(string Provider);

/// <summary>One <c>contributions.sidebar</c> entry.</summary>
/// <param name="Id">The section id; follows the settings key grammar and is unique within the plugin.</param>
/// <param name="Title">The header text the host draws.</param>
/// <param name="Provider">Passed to <c>ISidebarSectionProvider.CreateSidebarSection</c>.</param>
/// <param name="Command">A declared command the header button runs, or null for no button.</param>
public sealed record PluginSidebarSpec(string Id, string Title, string Provider, string? Command);

/// <summary><c>contributions.settingsPage</c>.</summary>
/// <param name="Provider">Passed to <c>ISettingsPageProvider.CreateSettingsPage</c>.</param>
public sealed record PluginSettingsPageSpec(string Provider);

public sealed record PluginCommandSpec(string Id, string Label, string Group, string? Shortcut);

public sealed record PluginContextMenuSpec(string Group, IReadOnlyList<PluginContextMenuItemSpec> Items);

public sealed record PluginContextMenuItemSpec(string Command, string Label, string When);

public sealed record PluginToolbarSpec(string Command, string Label, string Glyph, string Placement)
{
    /// <summary>
    /// A <c>when</c> expression (the context menu's grammar) that hides the button while it is false; empty shows it
    /// always.
    /// </summary>
    public string When { get; init; } = "";
}

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
    IReadOnlyList<string>? Options)
{
    /// <summary>
    /// A SumaFile settings key whose value the host copies into this setting once, while the setting has no value.
    /// Honoured only for plugins that <c>bundled.json</c> lists; never a <c>plugin.</c> key.
    /// </summary>
    public string? MigrateFrom { get; init; }
}

public sealed record ManifestReadResult(PluginManifest? Manifest, IReadOnlyList<string> Errors)
{
    public bool IsValid => Manifest is not null;
}
