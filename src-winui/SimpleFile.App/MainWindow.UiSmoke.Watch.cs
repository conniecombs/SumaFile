#if DEBUG
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;

namespace SimpleFile.App;

// A folder that changes from outside while a pane shows it: new rows, a renamed row that stays selected, more
// changes than are listed one by one, and a folder that goes away under the pane. Excluded from release builds.
public sealed partial class MainWindow
{
    private async Task SmokeLiveFolderChangesAsync(List<object> results, string root)
    {
        var workspace = _workspace!;
        var folder = Path.Combine(root, "watched");
        foreach (var name in new[] { "deleted", "renamed", "remade" })
        {
            Directory.CreateDirectory(Path.Combine(folder, name));
        }

        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
        {
            File.WriteAllText(Path.Combine(folder, name), name);
        }

        Task ShowsAsync(string path, int count) => SmokeRemoteWaitAsync(
            () => PathRules.PathsEqual(workspace.Primary.Path, path) && !workspace.Primary.ListingInProgress && PrimaryFiles.Count == count,
            () => $"{path} with {count} rows; the pane shows {PrimaryFiles.Count} of {workspace.Primary.Path}");
        // The watch follows the pane: a change made before the service watches the folder is not reported.
        async Task OpenWatchedAsync(string path, int count)
        {
            var before = _watchesEstablished;
            await workspace.NavigatePaneAsync(PaneId.Primary, path);
            SyncFromWorkspace();
            await ShowsAsync(path, count);
            await SmokeRemoteWaitAsync(
                () => _watchesEstablished > before && (_watchTargetPath ?? "").Contains(path, StringComparison.OrdinalIgnoreCase),
                () => $"the watch on {path}; asked for: {_watchTargetPath}");
        }

        string[] Selected(DetailsFileListView list) => [.. list.SelectedRows.Select(row => row.Name).Order(StringComparer.Ordinal)];
        try
        {
            workspace.ActivatePane(PaneId.Primary);
            workspace.SetFileListView(PaneId.Primary, "details");
            await OpenWatchedAsync(folder, 6);

            // 1. A file made from outside is one new row.
            File.WriteAllText(Path.Combine(folder, "outside.txt"), "made outside");
            await ShowsAsync(folder, 7);
            SmokeAssert(PrimaryFiles.Count(row => row.Name == "outside.txt") == 1, "The file made from outside is not one row");

            // 2. Two rows are selected and one of them is renamed from outside: both stay selected, the renamed
            // one under its new name.
            PrimaryDetailsFileList.SelectRows(PrimaryFiles.Where(row => row.Name is "b.txt" or "c.txt").ToHashSet());
            await SmokeRemoteWaitAsync(() => Selected(PrimaryDetailsFileList).SequenceEqual(["b.txt", "c.txt"]),
                () => $"b.txt and c.txt to be selected; selected: {string.Join(", ", Selected(PrimaryDetailsFileList))}");
            File.Move(Path.Combine(folder, "b.txt"), Path.Combine(folder, "moved-on.txt"));
            await SmokeRemoteWaitAsync(
                () => PrimaryFiles.Any(row => row.Name == "moved-on.txt") && PrimaryFiles.All(row => row.Name != "b.txt"),
                () => $"the renamed row; rows: {string.Join(", ", PrimaryFiles.Select(row => row.Name))}");
            await SmokeRemoteWaitAsync(() => Selected(PrimaryDetailsFileList).SequenceEqual(["c.txt", "moved-on.txt"]),
                () => $"the renamed row to stay selected; selected: {string.Join(", ", Selected(PrimaryDetailsFileList))}");
            SmokeAssert(PrimaryFiles.Count == 7, $"A rename changed the number of rows to {PrimaryFiles.Count}");

            // The same in a view whose list holds its selection by row.
            workspace.SetFileListView(PaneId.Primary, "list");
            SyncFromWorkspace();
            FileRow? row = null;
            await SmokeRemoteWaitAsync(
                () => PrimaryFileList.Visibility == Visibility.Visible
                    && (row = PrimaryFiles.FirstOrDefault(candidate => candidate.Name == "a.txt")) is not null
                    && PrimaryFileList.ContainerFromItem(row) is ListViewItem { ActualHeight: > 0 },
                () => "the a.txt row in the List view");
            PrimaryFileList.SelectedItem = row;
            File.Move(Path.Combine(folder, "a.txt"), Path.Combine(folder, "a-renamed.txt"));
            await SmokeRemoteWaitAsync(
                () => PrimaryFileList.SelectedItems.OfType<FileRow>().Select(selected => selected.Name).SequenceEqual(["a-renamed.txt"]),
                () => $"the renamed row to stay selected in the List view; selected: {string.Join(", ", PrimaryFileList.SelectedItems.OfType<FileRow>().Select(selected => selected.Name))}");
            workspace.SetFileListView(PaneId.Primary, "details");
            SyncFromWorkspace();

            // 3. More changes at once than are told one by one: the folder is read again, once, and shows them all.
            const int Burst = 700;
            for (var index = 0; index < Burst; index++)
            {
                File.WriteAllText(Path.Combine(folder, $"burst-{index:D4}.txt"), "");
            }

            await ShowsAsync(folder, 7 + Burst);
            foreach (var file in Directory.EnumerateFiles(folder, "burst-*.txt"))
            {
                File.Delete(file);
            }

            await ShowsAsync(folder, 7);

            // 4. The folder the pane shows is deleted from outside: the pane goes to the folder above it.
            var deleted = Path.Combine(folder, "deleted");
            await OpenWatchedAsync(deleted, 0);
            Directory.Delete(deleted);
            await ShowsAsync(folder, 6);
            SmokeAssert(workspace.StatusMessage == "\"deleted\" is no longer there.", $"The status after the folder was deleted reads: {workspace.StatusMessage}");
            SmokeAssert(!workspace.Primary.History.Any(path => PathRules.PathsEqual(path, deleted)), "The deleted folder is still in the pane's history");

            // 5. It is renamed from outside. The folder keeps reporting nothing about itself, so this is noticed by
            // asking it, every two seconds.
            var renamed = Path.Combine(folder, "renamed");
            await OpenWatchedAsync(renamed, 0);
            Directory.Move(renamed, Path.Combine(folder, "renamed-to"));
            await ShowsAsync(folder, 6);
            SmokeAssert(PrimaryFiles.Any(candidate => candidate.Name == "renamed-to"), "The folder above does not show the renamed folder");

            // 6. It is removed and made again, as a build does with its output folder: the pane stays, shows what
            // is in the new folder, and goes on following it.
            var remade = Path.Combine(folder, "remade");
            await OpenWatchedAsync(remade, 0);
            var watches = _watchesEstablished;
            Directory.Delete(remade);
            Directory.CreateDirectory(remade);
            File.WriteAllText(Path.Combine(remade, "built.txt"), "built");
            await ShowsAsync(remade, 1);
            await SmokeRemoteWaitAsync(() => _watchesEstablished > watches, () => "the new folder to be watched");
            File.WriteAllText(Path.Combine(remade, "built-later.txt"), "built later");
            await ShowsAsync(remade, 2);

            results.Add(new { scenario = "live-folder-changes", burst = Burst, passed = true });
        }
        finally
        {
            workspace.SetFileListView(PaneId.Primary, "details");
            await workspace.NavigatePaneAsync(PaneId.Primary, root);
            SyncFromWorkspace();
        }
    }
}
#endif
