namespace SimpleFile.Core;

/// <summary>
/// One plugin panel as the host renders it from the manifest, before any plugin code loads.
/// </summary>
/// <param name="PluginId">The plugin that contributes the panel.</param>
/// <param name="PanelId">The panel's id in the manifest's <c>contributions.panels</c>.</param>
/// <param name="Title">The header text, from the manifest.</param>
/// <param name="Placement"><c>bottom</c> or <c>right</c> of the file list.</param>
/// <param name="Detachable">Whether the host offers to move the panel into its own window.</param>
/// <param name="CommandId">The host command id, <c>plugin:&lt;id&gt;:&lt;command&gt;</c>, that toggles the panel, or null.</param>
/// <param name="Epoch">
/// The plugin record's epoch. It changes whenever the host retires the plugin's runtime, so a panel whose epoch
/// changed must be created again.
/// </param>
public sealed record PluginPanel(
    string PluginId,
    string PanelId,
    string Title,
    string Placement,
    bool Detachable,
    string? CommandId,
    int Epoch);

/// <summary>One enabled plugin's <c>contributions.rowDecorations</c>.</summary>
/// <param name="PluginId">The plugin that decorates rows.</param>
/// <param name="Provider">The manifest's <c>provider</c>, passed back to the plugin.</param>
/// <param name="Epoch">The plugin record's epoch (see <see cref="PluginPanel.Epoch"/>).</param>
public sealed record PluginDecorationProvider(string PluginId, string Provider, int Epoch);
