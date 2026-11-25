namespace SumaFile.Remote.Model;

/// <summary>
/// The widths the manager's two splitters allow. The first sets the profiles list's width; the second shares what is
/// left between the local and the remote list, as weights, so the two keep their proportion when the window is
/// resized. Each list keeps room for its Name, Size and Modified columns.
/// </summary>
public static class RemotePaneLayout
{
    public const double ProfilesDefaultWidth = 260;
    public const double ProfilesMinWidth = 180;
    public const double ProfilesMaxWidth = 420;

    /// <summary>The narrowest a file list gets: its fixed columns and a readable piece of the name.</summary>
    public const double PaneMinWidth = 360;

    /// <summary>The width of a splitter's column.</summary>
    public const double SplitterWidth = 12;

    /// <summary>How far an arrow key moves a splitter.</summary>
    public const double KeyStep = 24;

    /// <summary>
    /// The profiles list's width for a requested one, in a row <paramref name="totalWidth"/> wide that also holds the
    /// two splitters and the two file lists.
    /// </summary>
    public static double ClampProfiles(double requested, double totalWidth)
    {
        var room = totalWidth - 2 * SplitterWidth - 2 * PaneMinWidth;
        var max = Math.Clamp(double.IsFinite(room) ? room : ProfilesMaxWidth, ProfilesMinWidth, ProfilesMaxWidth);
        return Math.Clamp(double.IsFinite(requested) ? requested : ProfilesDefaultWidth, ProfilesMinWidth, max);
    }

    /// <summary>
    /// The local and remote lists' widths when the local one is asked to be <paramref name="requestedLocal"/> wide and
    /// the two share <paramref name="available"/>. A row too narrow for two lists of the minimum width is halved.
    /// </summary>
    public static (double Local, double Remote) SplitPanes(double requestedLocal, double available)
    {
        if (!double.IsFinite(available) || available <= 0)
        {
            return (1, 1);
        }

        if (available < 2 * PaneMinWidth)
        {
            return (available / 2, available / 2);
        }

        var local = Math.Clamp(double.IsFinite(requestedLocal) ? requestedLocal : available / 2, PaneMinWidth, available - PaneMinWidth);
        return (local, available - local);
    }
}
