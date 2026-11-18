namespace SimpleFile.Core;

/// <summary>Where the Properties dialog's count of a folder's contents stands.</summary>
public enum FolderCountState
{
    /// <summary>The walk through the folder is under way.</summary>
    Counting,

    /// <summary>The walk was stopped before it finished.</summary>
    Stopped,

    /// <summary>The walk failed.</summary>
    Failed,
}

/// <summary>
/// The words of the Properties dialog's Contents card while a folder is being counted. The count starts when the
/// dialog opens; its one button stops the walk, and starts it again once it is stopped or has failed.
/// </summary>
public static class FolderCountPresentation
{
    public static (string Status, string Button) For(FolderCountState state, string? error = null) => state switch
    {
        FolderCountState.Counting => ("Counting every file and folder inside, including subfolders…", "Stop"),
        FolderCountState.Stopped => ("Stopped before the count finished.", "Count"),
        _ => (string.IsNullOrWhiteSpace(error) ? "The count failed." : error, "Try again"),
    };
}
