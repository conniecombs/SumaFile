namespace SumaFile.Remote.Model;

/// <summary>One of the manager's two file lists.</summary>
public enum RemotePane
{
    Local,
    Remote,
}

/// <summary>What a key, a context menu item or a drop asks a file list to do.</summary>
public enum RemotePaneCommand
{
    None,
    Open,
    Parent,
    Refresh,
    Upload,
    Download,
    Rename,
    Delete,
    NewFolder,
}

/// <summary>
/// What a list's commands depend on: its selection, and whether the view model takes remote changes
/// (<see cref="RemoteManagerViewModel.CanMutateRemoteEntries"/>) and transfers
/// (<see cref="RemoteManagerViewModel.CanUseWorkspaceTransfers"/>) right now.
/// </summary>
public readonly record struct RemotePaneState(
    RemotePane Pane,
    int SelectedCount,
    bool SelectionIsOneFolder,
    bool CanMutateRemote,
    bool CanTransfer);

/// <summary>
/// Whether a list can run a command. The toolbar buttons are disabled by the same view model flags, and a key or a
/// menu item must not reach past a disabled button.
/// </summary>
public static class RemotePaneCommands
{
    public static bool IsAvailable(RemotePaneCommand command, RemotePaneState state) => state.Pane switch
    {
        RemotePane.Local => command switch
        {
            RemotePaneCommand.Open => state.SelectionIsOneFolder,
            RemotePaneCommand.Parent or RemotePaneCommand.Refresh => true,
            RemotePaneCommand.Upload => state.SelectedCount > 0 && state.CanTransfer,
            _ => false,
        },
        RemotePane.Remote => command switch
        {
            RemotePaneCommand.Open => state.SelectionIsOneFolder,
            RemotePaneCommand.Parent or RemotePaneCommand.Refresh or RemotePaneCommand.NewFolder => state.CanMutateRemote,
            RemotePaneCommand.Download => state.SelectedCount > 0 && state.CanTransfer,
            RemotePaneCommand.Rename => state.SelectedCount == 1 && state.CanMutateRemote,
            RemotePaneCommand.Delete => state.SelectedCount > 0 && state.CanMutateRemote,
            _ => false,
        },
        _ => false,
    };
}

/// <summary>
/// The keys of the two file lists, as in the app's own panes: Enter opens a folder, Backspace or Alt+Up goes to the
/// parent, F5 refreshes; in the remote list F2 renames, Delete deletes and Ctrl+Shift+N makes a folder. The local
/// list has no rename, delete or new folder, as the manager changes nothing on the local side but by a download.
/// </summary>
public static class RemotePaneKeys
{
    // Windows virtual-key codes; the model has no WinUI types.
    public const int Back = 0x08;
    public const int Enter = 0x0D;
    public const int Up = 0x26;
    public const int Delete = 0x2E;
    public const int N = 0x4E;
    public const int F2 = 0x71;
    public const int F5 = 0x74;

    public static RemotePaneCommand CommandFor(RemotePane pane, int key, bool control, bool alt, bool shift)
    {
        if (!control && !alt && !shift)
        {
            return key switch
            {
                Enter => RemotePaneCommand.Open,
                Back => RemotePaneCommand.Parent,
                F5 => RemotePaneCommand.Refresh,
                F2 when pane == RemotePane.Remote => RemotePaneCommand.Rename,
                Delete when pane == RemotePane.Remote => RemotePaneCommand.Delete,
                _ => RemotePaneCommand.None,
            };
        }

        if (alt && !control && !shift && key == Up)
        {
            return RemotePaneCommand.Parent;
        }

        return control && shift && !alt && key == N && pane == RemotePane.Remote
            ? RemotePaneCommand.NewFolder
            : RemotePaneCommand.None;
    }
}

/// <summary>A context menu item. <paramref name="Shortcut"/> is the key shown beside it, or empty.</summary>
public sealed record RemoteMenuEntry(RemotePaneCommand Command, string Text, string Shortcut, bool IsEnabled, bool SeparatorBefore);

/// <summary>
/// The context menu of a file list. Items that act on the selection are listed only when rows are selected; an item
/// the view model would refuse right now is listed disabled, so the menu keeps its shape while a transfer runs.
/// </summary>
public static class RemotePaneMenu
{
    public static IReadOnlyList<RemoteMenuEntry> Build(RemotePaneState state)
    {
        var entries = new List<RemoteMenuEntry>();
        var separate = false;

        void Add(RemotePaneCommand command, string text, string shortcut = "")
        {
            entries.Add(new RemoteMenuEntry(command, text, shortcut, RemotePaneCommands.IsAvailable(command, state), separate && entries.Count > 0));
            separate = false;
        }

        if (state.SelectionIsOneFolder)
        {
            Add(RemotePaneCommand.Open, "Open", "Enter");
        }

        if (state.SelectedCount > 0)
        {
            if (state.Pane == RemotePane.Local)
            {
                Add(RemotePaneCommand.Upload, "Upload");
            }
            else
            {
                Add(RemotePaneCommand.Download, "Download");
                if (state.SelectedCount == 1)
                {
                    Add(RemotePaneCommand.Rename, "Rename", "F2");
                }

                Add(RemotePaneCommand.Delete, "Delete", "Delete");
            }
        }

        separate = true;
        if (state.Pane == RemotePane.Remote)
        {
            Add(RemotePaneCommand.NewFolder, "New folder", "Ctrl+Shift+N");
        }

        Add(RemotePaneCommand.Parent, "Go to parent folder", "Backspace");
        Add(RemotePaneCommand.Refresh, "Refresh", "F5");
        return entries;
    }
}

/// <summary>Where a drag over a file list comes from.</summary>
public enum RemoteDragSource
{
    /// <summary>Something that is not files: text, a link, a drag from another part of the window.</summary>
    Other,

    /// <summary>Rows of the manager's local list.</summary>
    LocalPane,

    /// <summary>Rows of the manager's remote list.</summary>
    RemotePane,

    /// <summary>Files from outside the window: Explorer, or a file pane of the app.</summary>
    Files,
}

/// <summary>What a drop does. <see cref="Command"/> is Upload or Download when <see cref="Accepted"/>.</summary>
public readonly record struct RemoteDropDecision(bool Accepted, RemotePaneCommand Command, string Caption)
{
    public static RemoteDropDecision Refused { get; } = new(false, RemotePaneCommand.None, "");
}

/// <summary>
/// The drops the two lists take. Local rows, or files from outside, dropped on the remote list are uploaded into
/// the remote folder shown; remote rows dropped on the local list are downloaded into the local folder shown. A list
/// does not take its own rows, and the local list does not take files from outside: copying between local folders
/// is the app's job, not the manager's.
/// </summary>
public static class RemoteDrop
{
    public static RemoteDropDecision Decide(
        RemotePane target,
        RemoteDragSource source,
        bool canMutateRemote,
        string localPath,
        string remotePath)
    {
        if (!canMutateRemote)
        {
            return RemoteDropDecision.Refused;
        }

        return (target, source) switch
        {
            (RemotePane.Remote, RemoteDragSource.LocalPane or RemoteDragSource.Files) =>
                new RemoteDropDecision(true, RemotePaneCommand.Upload, $"Upload to {remotePath}"),
            (RemotePane.Local, RemoteDragSource.RemotePane) when !string.IsNullOrWhiteSpace(localPath) =>
                new RemoteDropDecision(true, RemotePaneCommand.Download, $"Download to {localPath}"),
            _ => RemoteDropDecision.Refused,
        };
    }
}
