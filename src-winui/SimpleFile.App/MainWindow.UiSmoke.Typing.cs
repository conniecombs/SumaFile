#if DEBUG
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SimpleFile.Core;
using Windows.Foundation;

namespace SimpleFile.App;

// Suggestions while typing in the omnibar and in a pane's path box, renaming in place in the List, Tiles and Content views,
// and moving a Details column: the parts of audit U6 that the keyboard reaches. Excluded from release builds.
public sealed partial class MainWindow
{
    private async Task SmokeTypingAndColumnsAsync(List<object> results, string root)
    {
        var workspace = _workspace!;
        var folder = Path.Combine(root, "typing");
        foreach (var name in new[] { "alpha", "alpine", "beta" })
        {
            Directory.CreateDirectory(Path.Combine(folder, name));
        }

        File.WriteAllText(Path.Combine(folder, "note.txt"), "note");
        bool Focused(Control box) => ReferenceEquals(FocusManager.GetFocusedElement(box.XamlRoot), box);
        string Lines() => string.Join(" | ", _suggestions.Select(suggestion => suggestion.Text));
        Task ListedAsync(int count) => SmokeRemoteWaitAsync(
            () => PathRules.PathsEqual(workspace.Primary.Path, folder) && !workspace.Primary.ListingInProgress && PrimaryFiles.Count == count,
            () => $"the typing folder with {count} rows; it shows {PrimaryFiles.Count} of {workspace.Primary.Path}");
        try
        {
            workspace.ActivatePane(PaneId.Primary);
            workspace.SetFileListView(PaneId.Primary, "details");
            await workspace.NavigatePaneAsync(PaneId.Primary, folder);
            SyncFromWorkspace();
            await ListedAsync(4);

            // 1. A bare name typed in the omnibar suggests the folders of this folder that start with it. Down
            // chooses the first, Tab takes it, and the box stays in use for the next name.
            FocusOmnibar(OmnibarMode.Navigate);
            await SmokeRemoteWaitAsync(() => _omnibarFocused && Focused(OmnibarInput), () => "the focus in the omnibar");
            OmnibarInput.Text = "al";
            await SmokeRemoteWaitAsync(
                () => _suggestionsOpen && ReferenceEquals(_suggestBox, OmnibarInput) && _suggestions.Count == 2,
                () => $"two folder suggestions for 'al', open: {_suggestionsOpen}, lines: [{Lines()}]");
            SmokeAssert(_suggestions.All(suggestion => suggestion.Kind == OmnibarSuggestionKind.Folder)
                && _suggestions.Select(suggestion => Path.GetFileName(suggestion.Value)).SequenceEqual(["alpha", "alpine"]),
                $"The suggestions for 'al' are [{Lines()}]");
            SmokeAssert(_pathSuggestList!.SelectedIndex == -1, "A suggestion is chosen before any key: Enter would not run what was typed");
            await SmokePressKeyAsync(OmnibarInput, SmokeVkDown, "the omnibar");
            await SmokeRemoteWaitAsync(() => _pathSuggestList.SelectedIndex == 0, () => $"Down to choose the first suggestion, chosen: {_pathSuggestList.SelectedIndex}");
            await SmokePressKeyAsync(OmnibarInput, SmokeVkTab, "the omnibar");
            var alpha = Path.Combine(folder, "alpha") + Path.DirectorySeparatorChar;
            await SmokeRemoteWaitAsync(() => OmnibarInput.Text == alpha, () => $"Tab to fill in '{alpha}', the omnibar reads '{OmnibarInput.Text}'");
            SmokeAssert(Focused(OmnibarInput) && OmnibarInput.SelectionStart == alpha.Length, "Taking a suggestion moved the focus or the caret out of the omnibar");
            await SmokePressKeyAsync(OmnibarInput, SmokeVkEnter, "the omnibar");
            await SmokeRemoteWaitAsync(() => PathRules.PathsEqual(workspace.Primary.Path, Path.Combine(folder, "alpha")),
                () => $"Enter to open the filled-in folder, the pane shows {workspace.Primary.Path}");
            await workspace.NavigatePaneAsync(PaneId.Primary, folder);
            SyncFromWorkspace();
            await ListedAsync(4);

            // 2. After '>' it suggests commands. Esc closes the list and leaves what was typed.
            FocusOmnibar(OmnibarMode.Navigate);
            await SmokeRemoteWaitAsync(() => _omnibarFocused && Focused(OmnibarInput), () => "the focus in the omnibar again");
            OmnibarInput.Text = ">refresh";
            await SmokeRemoteWaitAsync(
                () => _suggestionsOpen && _suggestions.Count > 0 && _suggestions.All(suggestion => suggestion.Kind == OmnibarSuggestionKind.Command),
                () => $"command suggestions for '>refresh', open: {_suggestionsOpen}, lines: [{Lines()}]");
            SmokeAssert(_suggestions[0].Value == "refresh", $"The first suggestion for '>refresh' is {_suggestions[0].Value}");
            await SmokePressKeyAsync(OmnibarInput, SmokeVkEscape, "the omnibar");
            await SmokeRemoteWaitAsync(() => !_suggestionsOpen, () => "Esc to close the suggestions");
            SmokeAssert(OmnibarInput.Text == ">refresh", $"Esc on the open list changed the omnibar's text to '{OmnibarInput.Text}'");
            await SmokePressKeyAsync(OmnibarInput, SmokeVkEscape, "the omnibar");
            await SmokeRemoteWaitAsync(() => PathRules.PathsEqual(OmnibarInput.Text, folder), () => $"a second Esc to put the path back, the omnibar reads '{OmnibarInput.Text}'");

            // 3. A pane's path box: the same list, and Tab takes its first line without Down.
            BeginPathEdit(PaneId.Primary);
            await SmokeRemoteWaitAsync(() => Focused(PrimaryPathInput), () => "the focus in the path box");
            PrimaryPathInput.Text = Path.Combine(folder, "be");
            await SmokeRemoteWaitAsync(
                () => _suggestionsOpen && ReferenceEquals(_suggestBox, PrimaryPathInput) && _suggestions.Count == 1,
                () => $"one suggestion for '...\\be' under the path box, open: {_suggestionsOpen}, lines: [{Lines()}]");
            await SmokePressKeyAsync(PrimaryPathInput, SmokeVkTab, "the path box");
            var beta = Path.Combine(folder, "beta") + Path.DirectorySeparatorChar;
            await SmokeRemoteWaitAsync(() => PrimaryPathInput.Text == beta, () => $"Tab to fill in '{beta}', the path box reads '{PrimaryPathInput.Text}'");
            SmokeAssert(_editingPrimaryPath && Focused(PrimaryPathInput), "Taking a suggestion ended the path edit");
            EndPathEdit(PaneId.Primary, reset: true);
            HidePathSuggestions();

            // 4. F2 renames in place in the List view, with the name selected up to its extension.
            await SmokeRenameInPlaceAsync(folder, "list", "note.txt", "note", "renamed.txt");

            // 5. And in the Tiles view, where the box is under the icon, over the name.
            await SmokeRenameInPlaceAsync(folder, "tiles", "renamed.txt", "renamed", "tile.txt");
            await SmokeRenameInPlaceAsync(folder, "content", "tile.txt", "tile", "content.txt");

            // 6. A Details column moves one place from its header's menu, and never in front of Name.
            workspace.SetFileListView(PaneId.Primary, "details");
            SyncFromWorkspace();
            await SmokeRemoteWaitAsync(() => PrimaryDetailsFileList.Visibility == Visibility.Visible && SmokeColumnOrder().Contains("size"),
                () => $"the Details headers, they are [{SmokeColumnOrder()}]");
            var before = SmokeColumnOrder();
            SmokeAssert(before.StartsWith("name,size,date", StringComparison.Ordinal), $"The Details columns start as [{before}]");
            MoveColumnBy(PaneId.Primary, "size", 1);
            await SmokeRemoteWaitAsync(() => SmokeColumnOrder().StartsWith("name,date,size", StringComparison.Ordinal),
                () => $"Size to move right of Date, the headers are [{SmokeColumnOrder()}]");
            MoveColumnBy(PaneId.Primary, "size", -1);
            MoveColumnBy(PaneId.Primary, "size", -1);
            await SmokeRemoteWaitAsync(() => SmokeColumnOrder() == before, () => $"Size to move back and stop behind Name, the headers are [{SmokeColumnOrder()}]");
            SmokeAssert(workspace.ColumnsFor(PaneId.Primary).VisibleIds[0] == "name", "A column moved in front of Name");

            // 7. The sidebar's divider from the keyboard: the resize commands widen and narrow it by one step.
            var showedSidebar = false;
            if (!workspace.Settings.SidebarVisible)
            {
                await ToggleSidebarAsync();
                showedSidebar = true;
            }

            try
            {
                var sidebarWidth = workspace.Settings.SidebarWidth;
                await RunAppCommandAsync("sidebar-wider");
                SmokeAssert(Math.Abs(workspace.Settings.SidebarWidth - (sidebarWidth + UiSettings.ResizeCommandStep)) < 0.01
                    && Math.Abs(SidebarColumn.Width.Value - workspace.Settings.SidebarWidth) < 0.01,
                    $"'Make the sidebar wider' left it {workspace.Settings.SidebarWidth} wide (column {SidebarColumn.Width.Value}), from {sidebarWidth}");
                await RunAppCommandAsync("sidebar-narrower");
                SmokeAssert(Math.Abs(workspace.Settings.SidebarWidth - sidebarWidth) < 0.01 && Math.Abs(SidebarColumn.Width.Value - sidebarWidth) < 0.01,
                    $"'Make the sidebar narrower' left it {workspace.Settings.SidebarWidth} wide, expected {sidebarWidth} again");
            }
            finally
            {
                if (showedSidebar)
                {
                    await ToggleSidebarAsync();
                }
            }

            results.Add(new { scenario = "typing-and-columns", passed = true });
        }
        finally
        {
            HidePathSuggestions();
            workspace.SetFileListView(PaneId.Primary, "list");
            await workspace.NavigatePaneAsync(PaneId.Primary, Path.Combine(root, "empty"));
            SyncFromWorkspace();
        }
    }

    /// <summary>The ids of the Details header's columns, left to right.</summary>
    private string SmokeColumnOrder() =>
        string.Join(",", PrimaryColumnHeader.Children.OfType<Grid>().Select(cell => cell.Tag as string).Where(id => id is not null));

    /// <summary>
    /// Renames <paramref name="name"/> in place in <paramref name="view"/>, as F2 does: the box opens over the name
    /// with <paramref name="stem"/> selected, and Enter renames the file to <paramref name="newName"/>.
    /// </summary>
    private async Task SmokeRenameInPlaceAsync(string folder, string view, string name, string stem, string newName)
    {
        var workspace = _workspace!;
        workspace.SetFileListView(PaneId.Primary, view);
        SyncFromWorkspace();
        FileRow? row = null;
        await SmokeRemoteWaitAsync(
            () => PrimaryFileList.Visibility == Visibility.Visible
                && (row = PrimaryFiles.FirstOrDefault(candidate => candidate.Name == name)) is not null
                && PrimaryFileList.ContainerFromItem(row) is ListViewItem { ActualHeight: > 0 },
            () => $"the {name} row in the {view} view");
        PrimaryFileList.SelectedItem = row;
        PrimaryFileList.Focus(FocusState.Programmatic);
        SmokeAssert(TryBeginInlineRename(), $"F2 did not start renaming in place in the {view} view");
        await SmokeRemoteWaitAsync(() => _inlineRenameView is { IsRenaming: true }, () => $"the rename box in the {view} view");
        var rowView = _inlineRenameView!;
        var box = (TextBox)rowView.FindName("RenameBox");
        await SmokeRemoteWaitAsync(() => box.SelectedText == stem && Equals(FocusManager.GetFocusedElement(box.XamlRoot), box),
            () => $"the rename box to have the focus with '{stem}' selected; '{box.SelectedText}' is selected of '{box.Text}'");

        // The box is over the name, wherever this view puts it: beside the icon in a row, under it in a tile.
        var label = SmokeDescendants<TextBlock>(rowView).First(text => text.Text == name);
        var labelTop = label.TransformToVisual(rowView).TransformPoint(new Point(0, 0)).Y;
        var boxTop = box.TransformToVisual(rowView).TransformPoint(new Point(0, 0)).Y;
        SmokeAssert(Math.Abs(boxTop - RenameBoxLayout.Top(labelTop)) <= 1.5,
            $"In the {view} view the rename box (top {boxTop:0}, height {box.ActualHeight:0}) is not over the name (top {labelTop:0})");

        box.Text = newName;
        await SmokePressKeyAsync(box, SmokeVkEnter, "the rename box");
        await SmokeWaitWithinAsync(
            () => Task.FromResult(File.Exists(Path.Combine(folder, newName)) && !File.Exists(Path.Combine(folder, name))
                && PrimaryFiles.Any(candidate => candidate.Name == newName)),
            TimeSpan.FromSeconds(15),
            () => $"Enter to rename {name} to {newName} in the {view} view");
        SmokeAssert(!IsInlineRenameActive, $"The rename box is still open in the {view} view after Enter");
    }
}
#endif
