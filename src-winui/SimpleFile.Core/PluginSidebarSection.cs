namespace SimpleFile.Core;

/// <summary>
/// One plugin sidebar section as the host renders it from the manifest, before any plugin code loads.
/// </summary>
/// <param name="PluginId">The plugin that contributes the section.</param>
/// <param name="SectionId">The section's id in the manifest's <c>contributions.sidebar</c>.</param>
/// <param name="Title">The header text, from the manifest.</param>
/// <param name="CommandId">The header button's host command id, <c>plugin:&lt;id&gt;:&lt;command&gt;</c>, or null for none.</param>
/// <param name="CommandLabel">That command's label, or null when <paramref name="CommandId"/> is null.</param>
/// <param name="Epoch">
/// The plugin record's epoch. It changes whenever the host retires the plugin's runtime, so a section whose epoch
/// changed must be rendered again with a new body.
/// </param>
public sealed record PluginSidebarSection(
    string PluginId,
    string SectionId,
    string Title,
    string? CommandId,
    string? CommandLabel,
    int Epoch);
