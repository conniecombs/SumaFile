using System.Globalization;
using SumaFile.Remote.Model;
using Xunit;

namespace SimpleFile.Tests.Remote;

/// <summary>
/// The rules behind the FTP/SFTP manager's file lists that need no window: cell text, keys, context menus, drops and
/// splitter widths.
/// </summary>
public class RemotePaneGestureTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static RemotePaneState Local(int selected = 0, bool oneFolder = false, bool canTransfer = true) =>
        new(RemotePane.Local, selected, oneFolder, CanMutateRemote: canTransfer, CanTransfer: canTransfer);

    private static RemotePaneState Remote(int selected = 0, bool oneFolder = false, bool canMutate = true, bool canTransfer = true) =>
        new(RemotePane.Remote, selected, oneFolder, canMutate, canTransfer);

    [Theory]
    [InlineData(0UL, "0 B")]
    [InlineData(1023UL, "1023 B")]
    [InlineData(1024UL, "1.0 KB")]
    [InlineData(1536UL, "1.5 KB")]
    [InlineData(5UL * 1024 * 1024, "5.0 MB")]
    [InlineData(3UL * 1024 * 1024 * 1024, "3.0 GB")]
    public void Size_IsWrittenAsTheFilePanesWriteIt(ulong bytes, string expected)
    {
        Assert.Equal(expected, RemoteEntryText.Size(bytes, Invariant));
        Assert.Equal(expected, RemoteEntryText.Size(new RemoteFileEntry { Size = bytes }, Invariant));
    }

    [Fact]
    public void Size_AFolderShowsNone()
    {
        Assert.Equal("", RemoteEntryText.Size(new RemoteFileEntry { IsDir = true, Size = 4096 }, Invariant));
    }

    [Fact]
    public void Modified_IsALocalShortDateAndTime()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");

        Assert.Equal("11/24/2026 12:30", RemoteEntryText.Modified("2026-11-24T10:30:45Z", zone, Invariant));
        Assert.Equal("11/25/2026 01:00", RemoteEntryText.Modified("2026-11-24T23:00:00+00:00", zone, Invariant));
        // The FTP listing code writes fractions of a second.
        Assert.Equal("11/24/2026 12:30", RemoteEntryText.Modified("2026-11-24T10:30:45.123456789+00:00", zone, Invariant));
    }

    [Fact]
    public void Modified_TextThatIsNotATimeIsShownAsItIs()
    {
        Assert.Equal("", RemoteEntryText.Modified(""));
        Assert.Equal("", RemoteEntryText.Modified(null));
        Assert.Equal("yesterday", RemoteEntryText.Modified("yesterday"));
    }

    [Fact]
    public void Keys_BothListsOpenGoUpAndRefresh()
    {
        foreach (var pane in new[] { RemotePane.Local, RemotePane.Remote })
        {
            Assert.Equal(RemotePaneCommand.Open, RemotePaneKeys.CommandFor(pane, RemotePaneKeys.Enter, false, false, false));
            Assert.Equal(RemotePaneCommand.Parent, RemotePaneKeys.CommandFor(pane, RemotePaneKeys.Back, false, false, false));
            Assert.Equal(RemotePaneCommand.Parent, RemotePaneKeys.CommandFor(pane, RemotePaneKeys.Up, false, alt: true, false));
            Assert.Equal(RemotePaneCommand.Refresh, RemotePaneKeys.CommandFor(pane, RemotePaneKeys.F5, false, false, false));
            // A plain Up moves the selection, and a modified Enter or F5 is not ours.
            Assert.Equal(RemotePaneCommand.None, RemotePaneKeys.CommandFor(pane, RemotePaneKeys.Up, false, false, false));
            Assert.Equal(RemotePaneCommand.None, RemotePaneKeys.CommandFor(pane, RemotePaneKeys.Enter, control: true, false, false));
            Assert.Equal(RemotePaneCommand.None, RemotePaneKeys.CommandFor(pane, RemotePaneKeys.F5, false, false, shift: true));
        }
    }

    [Fact]
    public void Keys_OnlyTheRemoteListRenamesDeletesAndMakesFolders()
    {
        Assert.Equal(RemotePaneCommand.Rename, RemotePaneKeys.CommandFor(RemotePane.Remote, RemotePaneKeys.F2, false, false, false));
        Assert.Equal(RemotePaneCommand.Delete, RemotePaneKeys.CommandFor(RemotePane.Remote, RemotePaneKeys.Delete, false, false, false));
        Assert.Equal(RemotePaneCommand.NewFolder, RemotePaneKeys.CommandFor(RemotePane.Remote, RemotePaneKeys.N, control: true, false, shift: true));

        Assert.Equal(RemotePaneCommand.None, RemotePaneKeys.CommandFor(RemotePane.Local, RemotePaneKeys.F2, false, false, false));
        Assert.Equal(RemotePaneCommand.None, RemotePaneKeys.CommandFor(RemotePane.Local, RemotePaneKeys.Delete, false, false, false));
        Assert.Equal(RemotePaneCommand.None, RemotePaneKeys.CommandFor(RemotePane.Local, RemotePaneKeys.N, control: true, false, shift: true));
        // Shift+Delete is not a quieter Delete.
        Assert.Equal(RemotePaneCommand.None, RemotePaneKeys.CommandFor(RemotePane.Remote, RemotePaneKeys.Delete, false, false, shift: true));
    }

    [Fact]
    public void Commands_FollowTheFlagsThatDisableTheToolbarButtons()
    {
        var busy = Remote(selected: 2, canMutate: false, canTransfer: false);
        foreach (var command in new[]
                 {
                     RemotePaneCommand.Parent, RemotePaneCommand.Refresh, RemotePaneCommand.NewFolder,
                     RemotePaneCommand.Download, RemotePaneCommand.Rename, RemotePaneCommand.Delete,
                 })
        {
            Assert.False(RemotePaneCommands.IsAvailable(command, busy), $"{command} runs while the view model is busy.");
        }

        var idle = Remote(selected: 2);
        Assert.True(RemotePaneCommands.IsAvailable(RemotePaneCommand.Download, idle));
        Assert.True(RemotePaneCommands.IsAvailable(RemotePaneCommand.Delete, idle));
        Assert.False(RemotePaneCommands.IsAvailable(RemotePaneCommand.Rename, idle), "Rename takes one item.");
        Assert.True(RemotePaneCommands.IsAvailable(RemotePaneCommand.Rename, Remote(selected: 1)));
        Assert.False(RemotePaneCommands.IsAvailable(RemotePaneCommand.Delete, Remote()), "Delete needs a selection.");
        Assert.False(RemotePaneCommands.IsAvailable(RemotePaneCommand.Upload, idle), "The remote list does not upload.");

        // The local list browses whatever the session does, and uploads only when a transfer may start.
        var offline = Local(selected: 1, canTransfer: false);
        Assert.True(RemotePaneCommands.IsAvailable(RemotePaneCommand.Parent, offline));
        Assert.True(RemotePaneCommands.IsAvailable(RemotePaneCommand.Refresh, offline));
        Assert.False(RemotePaneCommands.IsAvailable(RemotePaneCommand.Upload, offline));
        Assert.True(RemotePaneCommands.IsAvailable(RemotePaneCommand.Upload, Local(selected: 1)));
        Assert.False(RemotePaneCommands.IsAvailable(RemotePaneCommand.Upload, Local()));
        Assert.False(RemotePaneCommands.IsAvailable(RemotePaneCommand.Delete, Local(selected: 1)));
        Assert.False(RemotePaneCommands.IsAvailable(RemotePaneCommand.Rename, Local(selected: 1)));
    }

    [Fact]
    public void Menu_ListsWhatTheSelectionAllows()
    {
        static string[] Texts(RemotePaneState state) => RemotePaneMenu.Build(state).Select(entry => entry.Text).ToArray();

        Assert.Equal(["Go to parent folder", "Refresh"], Texts(Local()));
        Assert.Equal(["Upload", "Go to parent folder", "Refresh"], Texts(Local(selected: 3)));
        Assert.Equal(["Open", "Upload", "Go to parent folder", "Refresh"], Texts(Local(selected: 1, oneFolder: true)));

        Assert.Equal(["New folder", "Go to parent folder", "Refresh"], Texts(Remote()));
        Assert.Equal(["Download", "Rename", "Delete", "New folder", "Go to parent folder", "Refresh"], Texts(Remote(selected: 1)));
        Assert.Equal(["Open", "Download", "Rename", "Delete", "New folder", "Go to parent folder", "Refresh"], Texts(Remote(selected: 1, oneFolder: true)));
        Assert.Equal(["Download", "Delete", "New folder", "Go to parent folder", "Refresh"], Texts(Remote(selected: 4)));
    }

    [Fact]
    public void Menu_SeparatesTheSelectionsItemsAndShowsTheKeys()
    {
        var menu = RemotePaneMenu.Build(Remote(selected: 1));

        Assert.Equal("New folder", Assert.Single(menu, entry => entry.SeparatorBefore).Text);
        Assert.Equal("F2", menu.Single(entry => entry.Command == RemotePaneCommand.Rename).Shortcut);
        Assert.Equal("Delete", menu.Single(entry => entry.Command == RemotePaneCommand.Delete).Shortcut);
        Assert.Equal("F5", menu.Single(entry => entry.Command == RemotePaneCommand.Refresh).Shortcut);
        // Nothing above it to be separated from.
        Assert.DoesNotContain(RemotePaneMenu.Build(Remote()), entry => entry.SeparatorBefore);
        Assert.DoesNotContain(RemotePaneMenu.Build(Local()), entry => entry.SeparatorBefore);
    }

    [Fact]
    public void Menu_KeepsItsItemsAndDisablesThemWhileTheViewModelIsBusy()
    {
        var menu = RemotePaneMenu.Build(Remote(selected: 1, canMutate: false, canTransfer: false));

        Assert.Equal(["Download", "Rename", "Delete", "New folder", "Go to parent folder", "Refresh"], menu.Select(entry => entry.Text));
        Assert.All(menu, entry => Assert.False(entry.IsEnabled, $"{entry.Text} is enabled while busy."));
        Assert.All(RemotePaneMenu.Build(Remote(selected: 1)), entry => Assert.True(entry.IsEnabled, $"{entry.Text} is disabled when idle."));
    }

    [Fact]
    public void Drop_UploadsIntoTheRemoteListAndDownloadsIntoTheLocalOne()
    {
        var upload = RemoteDrop.Decide(RemotePane.Remote, RemoteDragSource.LocalPane, true, @"C:\Work", "/srv/www");
        Assert.Equal(new RemoteDropDecision(true, RemotePaneCommand.Upload, "Upload to /srv/www"), upload);

        // Files from Explorer or a file pane need no local folder in the manager.
        var fromOutside = RemoteDrop.Decide(RemotePane.Remote, RemoteDragSource.Files, true, "", "/");
        Assert.Equal(new RemoteDropDecision(true, RemotePaneCommand.Upload, "Upload to /"), fromOutside);

        var download = RemoteDrop.Decide(RemotePane.Local, RemoteDragSource.RemotePane, true, @"C:\Work", "/srv/www");
        Assert.Equal(new RemoteDropDecision(true, RemotePaneCommand.Download, @"Download to C:\Work"), download);
    }

    [Fact]
    public void Drop_RefusesEverythingElse()
    {
        // A list does not take its own rows.
        Assert.False(RemoteDrop.Decide(RemotePane.Local, RemoteDragSource.LocalPane, true, @"C:\Work", "/").Accepted);
        Assert.False(RemoteDrop.Decide(RemotePane.Remote, RemoteDragSource.RemotePane, true, @"C:\Work", "/").Accepted);
        // Copying local files into a local folder is the app's job.
        Assert.False(RemoteDrop.Decide(RemotePane.Local, RemoteDragSource.Files, true, @"C:\Work", "/").Accepted);
        Assert.False(RemoteDrop.Decide(RemotePane.Remote, RemoteDragSource.Other, true, @"C:\Work", "/").Accepted);
        // No download without a local folder, and nothing at all while not connected or busy.
        Assert.False(RemoteDrop.Decide(RemotePane.Local, RemoteDragSource.RemotePane, true, " ", "/").Accepted);
        Assert.False(RemoteDrop.Decide(RemotePane.Remote, RemoteDragSource.LocalPane, false, @"C:\Work", "/").Accepted);
        Assert.False(RemoteDrop.Decide(RemotePane.Local, RemoteDragSource.RemotePane, false, @"C:\Work", "/").Accepted);
        Assert.Equal(RemoteDropDecision.Refused, RemoteDrop.Decide(RemotePane.Remote, RemoteDragSource.Files, false, "", "/"));
    }

    [Fact]
    public void Splitters_KeepEveryListWideEnoughForItsColumns()
    {
        // The window's narrowest content row: 1120 less its padding.
        const double narrowest = 1088;
        Assert.Equal(RemotePaneLayout.ProfilesMinWidth, RemotePaneLayout.ClampProfiles(40, narrowest));
        Assert.Equal(300, RemotePaneLayout.ClampProfiles(300, narrowest));
        // 1088 - two splitters - two lists of the minimum width leaves 344.
        Assert.Equal(344, RemotePaneLayout.ClampProfiles(400, narrowest));
        Assert.Equal(RemotePaneLayout.ProfilesMaxWidth, RemotePaneLayout.ClampProfiles(900, 2000));
        Assert.Equal(RemotePaneLayout.ProfilesDefaultWidth, RemotePaneLayout.ClampProfiles(double.NaN, 2000));
        // A row narrower than the window allows still gets a usable width.
        Assert.Equal(RemotePaneLayout.ProfilesMinWidth, RemotePaneLayout.ClampProfiles(300, 500));

        Assert.Equal((400, 500), RemotePaneLayout.SplitPanes(400, 900));
        Assert.Equal((RemotePaneLayout.PaneMinWidth, 540), RemotePaneLayout.SplitPanes(10, 900));
        Assert.Equal((540, RemotePaneLayout.PaneMinWidth), RemotePaneLayout.SplitPanes(880, 900));
        Assert.Equal((300, 300), RemotePaneLayout.SplitPanes(500, 600));
        Assert.Equal((1, 1), RemotePaneLayout.SplitPanes(200, 0));
        Assert.Equal((450, 450), RemotePaneLayout.SplitPanes(double.NaN, 900));
    }
}
