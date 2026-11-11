namespace SumaFile.Git.Model;

/// <summary>The controller's two questions to the user; <c>SumaFile.Git.Views.GitPrompts</c> shows them as dialogs.</summary>
public interface IGitPrompts
{
    /// <summary>The commit message the user typed, or null when they cancelled.</summary>
    Task<string?> PromptCommitMessageAsync(CancellationToken ct);

    /// <summary>Whether the user confirmed discarding <paramref name="rows"/>; Cancel is the default.</summary>
    Task<bool> ConfirmDiscardAsync(IReadOnlyList<GitChangeRow> rows, CancellationToken ct);
}
