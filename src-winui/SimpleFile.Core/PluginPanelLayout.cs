using System.Globalization;

namespace SimpleFile.Core;

/// <summary>
/// The WinUI-free rules behind the plugin panel host: the keys that name a rendered panel, its size limits, and which
/// panel a contribution change or a toggle command means.
/// </summary>
public static class PluginPanelLayout
{
    public const double BottomMinHeight = 200;
    public const double BottomDefaultHeight = 320;
    public const double BottomMaxFraction = 0.7;
    public const double RightMinWidth = 260;
    public const double RightDefaultWidth = 360;
    public const double RightMaxFraction = 0.6;

    /// <summary>The size of a detached panel's window.</summary>
    public const int WindowWidth = 1180;
    public const int WindowHeight = 740;

    /// <summary>
    /// The rendered panel's key, <c>&lt;pluginId&gt;:&lt;panelId&gt;:&lt;epoch&gt;</c>. A new epoch is a new key, so a
    /// plugin whose runtime was retired never reuses a body built by the old runtime.
    /// </summary>
    public static string Key(PluginPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        return $"{panel.PluginId}:{panel.PanelId}:{panel.Epoch.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The panel's key in the <see cref="PluginBodyLoadQueue"/>, <c>panel:</c> followed by <see cref="Key"/>: the slot
    /// prefix keeps a panel and a sidebar section with the same ids from dropping each other's load.
    /// </summary>
    public static string BodyLoadKey(PluginPanel panel) => "panel:" + Key(panel);

    /// <summary>The key per-panel state is kept under, <c>&lt;pluginId&gt;:&lt;panelId&gt;</c>: it outlives epochs.</summary>
    public static string StateKey(PluginPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        return $"{panel.PluginId}:{panel.PanelId}";
    }

    /// <summary>
    /// The docked bottom panel's height: at least <see cref="BottomMinHeight"/>, at most
    /// <see cref="BottomMaxFraction"/> of <paramref name="available"/> (never below the minimum). A size that is not a
    /// number becomes the default; an <paramref name="available"/> that is not laid out yet (zero, negative or not a
    /// number) only enforces the minimum, so a saved size survives the first layout pass.
    /// </summary>
    public static double ClampBottomHeight(double requested, double available) =>
        Clamp(requested, available, BottomMinHeight, BottomDefaultHeight, BottomMaxFraction);

    /// <summary>The docked right panel's width, by the same rule with <see cref="RightMinWidth"/> and <see cref="RightMaxFraction"/>.</summary>
    public static double ClampRightWidth(double requested, double available) =>
        Clamp(requested, available, RightMinWidth, RightDefaultWidth, RightMaxFraction);

    /// <summary>The wanted panel whose <see cref="Key"/> is <paramref name="shownKey"/>, or null when its plugin or epoch left.</summary>
    public static PluginPanel? StillWanted(IReadOnlyList<PluginPanel> wanted, string? shownKey)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        return shownKey is null
            ? null
            : wanted.FirstOrDefault(panel => string.Equals(Key(panel), shownKey, StringComparison.Ordinal));
    }

    /// <summary>The panel that <paramref name="hostCommandId"/> (<c>plugin:&lt;id&gt;:&lt;command&gt;</c>) toggles, or null.</summary>
    public static PluginPanel? ForCommand(IReadOnlyList<PluginPanel> wanted, string hostCommandId)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        return string.IsNullOrEmpty(hostCommandId)
            ? null
            : wanted.FirstOrDefault(panel => string.Equals(panel.CommandId, hostCommandId, StringComparison.Ordinal));
    }

    private static double Clamp(double requested, double available, double min, double fallback, double fraction)
    {
        var size = double.IsFinite(requested) ? requested : fallback;
        if (!double.IsFinite(available) || available <= 0)
        {
            return Math.Max(min, size);
        }

        return Math.Clamp(size, min, Math.Max(min, fraction * available));
    }
}
