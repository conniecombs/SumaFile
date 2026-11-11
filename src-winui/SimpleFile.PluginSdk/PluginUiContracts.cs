namespace SimpleFile.PluginSdk;

/// <summary>What the host passes to <see cref="ISidebarSectionProvider.CreateSidebarSection"/>.</summary>
/// <param name="SectionId">The section's <c>id</c> in <c>contributions.sidebar</c>.</param>
/// <param name="Provider">The section's <c>provider</c> in the manifest.</param>
public sealed record PluginSidebarSectionRequest(string SectionId, string Provider);

/// <summary>What the host passes to <see cref="ISettingsPageProvider.CreateSettingsPage"/>.</summary>
/// <param name="Provider">The <c>provider</c> of <c>contributions.settingsPage</c>.</param>
public sealed record PluginSettingsPageRequest(string Provider);

/// <summary>How the host presents a window handed to <see cref="IPluginWindows.Attach"/>.</summary>
/// <remarks>
/// The host checks the options before it tracks the window, and clamps instead of failing: the width to 320-3840 and
/// the height to 240-2160 device-independent pixels, and the title to 120 characters. A null, empty or whitespace
/// title becomes the plugin's manifest <c>name</c>.
/// </remarks>
/// <param name="Title">The window title, up to 120 characters; a blank one becomes the plugin's name.</param>
/// <param name="Width">
/// The initial width in device-independent pixels, 320 to 3840; the host scales it for the display.
/// </param>
/// <param name="Height">
/// The initial height in device-independent pixels, 240 to 2160; the host scales it for the display.
/// </param>
public sealed record PluginWindowOptions(string Title, int Width, int Height);

/// <summary>Which button closed a dialog shown through <see cref="IPluginDialogs.ShowDialogAsync"/>.</summary>
public enum PluginDialogResult
{
    /// <summary>The close button or Escape, or the host hid the dialog.</summary>
    None = 0,

    /// <summary>The primary button.</summary>
    Primary = 1,

    /// <summary>The secondary button.</summary>
    Secondary = 2,
}

/// <summary>What the host passes to <see cref="IPanelProvider.CreatePanel"/>.</summary>
/// <param name="PanelId">The panel's <c>id</c> in <c>contributions.panels</c>.</param>
/// <param name="Provider">The panel's <c>provider</c> in the manifest.</param>
/// <param name="Detached">True when the element goes into the panel's own window, false when it is docked.</param>
public sealed record PluginPanelRequest(string PanelId, string Provider, bool Detached);

/// <summary>What the host passes to <see cref="IRowDecorationProvider.GetDecorationsAsync"/>.</summary>
/// <param name="Provider">The <c>provider</c> of <c>contributions.rowDecorations</c>.</param>
/// <param name="FolderPath">The listed folder.</param>
/// <param name="Paths">The full paths of the folder's listed rows.</param>
public sealed record PluginDecorationRequest(string Provider, string FolderPath, IReadOnlyList<string> Paths);

/// <summary>One row's decoration.</summary>
/// <param name="Text">The short text the row shows, such as a status letter.</param>
/// <param name="BrushKey">
/// A theme brush key the host resolves for the text, so it follows the light and dark themes; null, or a key the host
/// does not offer to plugins, uses the muted text brush.
/// </param>
/// <param name="Tooltip">Optional longer text for the row's tooltip.</param>
public sealed record RowDecoration(string Text, string? BrushKey = null, string? Tooltip = null);

/// <summary>A provider's result for one folder.</summary>
/// <param name="Rows">
/// Decorations keyed by row path, as given in <see cref="PluginDecorationRequest.Paths"/>; keys are matched
/// case-insensitively and whatever the separators, and a key that names no listed row is ignored.
/// </param>
/// <param name="FolderFlags">
/// Flags for the folder as a whole, such as <c>inRepository</c>, which the plugin's own <c>when</c> expressions read
/// with <c>folderFlag(name)</c>. A flag must match <c>^[A-Za-z][A-Za-z0-9_-]{0,63}$</c>; any other is dropped, as are
/// duplicates. The host scopes flags by plugin, so one plugin's flags never satisfy another plugin's <c>when</c>.
/// </param>
public sealed record PluginFolderDecorations(IReadOnlyDictionary<string, RowDecoration> Rows, IReadOnlyList<string> FolderFlags)
{
    /// <summary>No row decorations and no folder flags.</summary>
    public static PluginFolderDecorations Empty { get; } =
        new(System.Collections.ObjectModel.ReadOnlyDictionary<string, RowDecoration>.Empty, []);
}
