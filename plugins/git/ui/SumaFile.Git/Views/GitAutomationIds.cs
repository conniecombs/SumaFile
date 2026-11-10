namespace SumaFile.Git.Views;

/// <summary>
/// Automation ids of the Git plugin's UI. The UI smoke finds controls by these exact strings, so they are part of the
/// plugin's test contract: change them only together with <c>MainWindow.UiSmoke.Git.cs</c>.
/// </summary>
public static class GitAutomationIds
{
    // Workbench panel.
    public const string Workbench = "Git.Workbench";
    public const string Branch = "Git.Branch";
    public const string Summary = "Git.Summary";
    public const string Refresh = "Git.Refresh";
    public const string Fetch = "Git.Fetch";
    public const string Pull = "Git.Pull";
    public const string Push = "Git.Push";
    public const string Commit = "Git.Commit";
    public const string Stage = "Git.Stage";
    public const string Unstage = "Git.Unstage";
    public const string Discard = "Git.Discard";
    public const string Diff = "Git.Diff";
    public const string Changes = "Git.Changes";
    public const string DiffTitle = "Git.DiffTitle";
    public const string DiffText = "Git.DiffText";
    public const string DiffSplitter = "Git.DiffSplitter";

    // Commit dialog.
    public const string CommitMessage = "Git.CommitMessage";
}
