namespace SumaFile.Git.Model;

/// <summary>The plugin's ten commands as the host exposes them (<c>plugin:&lt;id&gt;:&lt;command&gt;</c>).</summary>
public static class GitCommandIds
{
    /// <summary>Toggles the workbench panel; the host handles it (plugin.json <c>panels[].command</c>).</summary>
    public const string Panel = "plugin:com.sumafile.git:git-panel";
    public const string Refresh = "plugin:com.sumafile.git:git-refresh";
    public const string Fetch = "plugin:com.sumafile.git:git-fetch";
    public const string Pull = "plugin:com.sumafile.git:git-pull";
    public const string Push = "plugin:com.sumafile.git:git-push";
    public const string Commit = "plugin:com.sumafile.git:git-commit";
    public const string StageSelected = "plugin:com.sumafile.git:git-stage-selected";
    public const string UnstageSelected = "plugin:com.sumafile.git:git-unstage-selected";
    public const string DiscardSelected = "plugin:com.sumafile.git:git-discard-selected";
    public const string DiffSelected = "plugin:com.sumafile.git:git-diff-selected";

    /// <summary>The nine commands the plugin runs itself; every one but <see cref="Panel"/>.</summary>
    public static IReadOnlyList<string> Handled { get; } =
        [Refresh, Fetch, Pull, Push, Commit, StageSelected, UnstageSelected, DiscardSelected, DiffSelected];
}
