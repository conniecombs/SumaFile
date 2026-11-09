namespace SimpleFile.Core;

/// <summary>
/// The WinUI-free rules behind the sidebar's plugin sections: which rendered sections a contribution change adds or
/// removes, the keys that name them, and when a section's body may load plugin code.
/// </summary>
public static class PluginSidebarLayout
{
    /// <summary>
    /// The rendered section's key, <c>&lt;pluginId&gt;:&lt;sectionId&gt;:&lt;epoch&gt;</c>. A new epoch is a new key, so a
    /// plugin whose runtime was retired gets a new section, and never reuses a body built by the old runtime.
    /// </summary>
    public static string Key(PluginSidebarSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return $"{section.PluginId}:{section.SectionId}:{section.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The key the collapse state is saved under, <c>&lt;pluginId&gt;:&lt;sectionId&gt;</c>: it outlives epochs and restarts.
    /// </summary>
    public static string CollapseKey(PluginSidebarSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return $"{section.PluginId}:{section.SectionId}";
    }

    /// <summary>
    /// The sections to add (those of <paramref name="wanted"/> not shown yet, in wanted order) and the shown keys to
    /// remove (those no wanted section has, in shown order). A section kept under the same key is left alone.
    /// </summary>
    public static (IReadOnlyList<PluginSidebarSection> Add, IReadOnlyList<string> RemoveKeys) Diff(
        IReadOnlyList<PluginSidebarSection> wanted,
        IReadOnlyCollection<string> shownKeys)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(shownKeys);
        var shown = new HashSet<string>(shownKeys, StringComparer.Ordinal);
        var wantedKeys = new HashSet<string>(StringComparer.Ordinal);
        var add = new List<PluginSidebarSection>();
        foreach (var section in wanted)
        {
            var key = Key(section);
            if (wantedKeys.Add(key) && !shown.Contains(key))
            {
                add.Add(section);
            }
        }

        var remove = shownKeys.Where(key => !wantedKeys.Contains(key)).Distinct(StringComparer.Ordinal).ToList();
        return (add, remove);
    }

    /// <summary>
    /// True when a section's body should be asked for now: startup has reached ready, the sidebar is shown, the
    /// section is expanded, and its body has not been asked for yet. Loading plugin code waits for all four.
    /// </summary>
    public static bool ShouldActivate(bool startupReady, bool sidebarVisible, bool collapsed, bool activated) =>
        startupReady && sidebarVisible && !collapsed && !activated;
}
