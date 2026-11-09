using SimpleFile.Core;
using SimpleFile.PluginHost.Manifest;

namespace SimpleFile.PluginHost.Contributions;

/// <summary>Turns enabled plugins' manifest contributions into the Core catalog slots.</summary>
public static class PluginContributionAdapter
{
    private const string PrimaryPlacement = "primary";

    public static IReadOnlyList<AppCommand> Commands(IEnumerable<PluginRecord> enabled) =>
        [.. Manifests(enabled).SelectMany(m => m.Contributions.Commands.Select(command =>
            new AppCommand(PluginManager.HostCommandId(m.Id, command.Id), command.Label, command.Group, command.Shortcut)))];

    public static IReadOnlyList<PluginContextMenuGroup> ContextMenus(IEnumerable<PluginRecord> enabled) =>
        [.. Manifests(enabled).SelectMany(m => m.Contributions.ContextMenu.Select(group =>
            new PluginContextMenuGroup(
                m.Id,
                group.Group,
                [.. group.Items.Select(item => new PluginContextMenuItem(
                    PluginManager.HostCommandId(m.Id, item.Command), item.Label, item.When))])))];

    public static IReadOnlyList<ToolbarAction> ToolbarActions(IEnumerable<PluginRecord> enabled) =>
        [.. Manifests(enabled).SelectMany(m => m.Contributions.Toolbar.Select(spec =>
        {
            var id = PluginManager.HostCommandId(m.Id, spec.Command);
            var group = m.Contributions.Commands.FirstOrDefault(c => c.Id == spec.Command)?.Group ?? m.Name;
            return new ToolbarAction(id, spec.Label, group, spec.Glyph, id);
        }))];

    public static IReadOnlyList<string> PrimaryToolbarCommandIds(IEnumerable<PluginRecord> enabled) =>
        [.. Manifests(enabled).SelectMany(m => m.Contributions.Toolbar
            .Where(spec => string.Equals(spec.Placement, PrimaryPlacement, StringComparison.OrdinalIgnoreCase))
            .Select(spec => PluginManager.HostCommandId(m.Id, spec.Command)))];

    /// <summary>
    /// Every sidebar section of the enabled plugins, plugin by plugin and in manifest order within each, with the
    /// header command as a host command id and the record's epoch.
    /// </summary>
    public static IReadOnlyList<PluginSidebarSection> SidebarSections(IEnumerable<PluginRecord> enabled) =>
        [.. enabled
            .Where(record => record.Discovered.Manifest is not null)
            .SelectMany(record =>
            {
                var manifest = record.Discovered.Manifest!;
                var epoch = record.Epoch;
                return manifest.Contributions.Sidebar.Select(spec =>
                {
                    var command = spec.Command is { } id
                        ? manifest.Contributions.Commands.FirstOrDefault(c => c.Id == id)
                        : null;
                    return new PluginSidebarSection(
                        manifest.Id,
                        spec.Id,
                        spec.Title,
                        command is null ? null : PluginManager.HostCommandId(manifest.Id, command.Id),
                        command?.Label,
                        epoch);
                });
            })];

    private static IEnumerable<PluginManifest> Manifests(IEnumerable<PluginRecord> enabled) =>
        enabled.Select(record => record.Discovered.Manifest).OfType<PluginManifest>();
}
