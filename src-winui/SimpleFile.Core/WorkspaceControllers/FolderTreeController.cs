using SimpleFile.Ipc;

namespace SimpleFile.Core;

internal sealed class FolderTreeController
{
    public List<FolderTreeItem> Rows { get; private set; } = [];

    public async Task<bool> LoadChildrenAsync(
        FileOperationService? fileOps,
        string path,
        CancellationToken cancellationToken = default)
    {
        if (fileOps is null)
        {
            return false;
        }

        try
        {
            var children = await fileOps.ListSubdirectoriesAsync(path, cancellationToken).ConfigureAwait(false);
            var expanded = new HashSet<string>(
                Rows.Where(row => row.Expanded).Select(row => row.Path),
                StringComparer.OrdinalIgnoreCase)
            {
                path,
            };
            var roots = Rows.Count == 0
                ? children.ToList()
                : MergeTree(Rows, path, children);
            Rows = FolderTree.Flatten(roots, expanded);
        }
        catch
        {
            // Tree is optional; listing still works.
        }

        return true;
    }

    public void ToggleExpanded(string path)
    {
        var expanded = new HashSet<string>(
            Rows.Where(row => row.Expanded).Select(row => row.Path),
            StringComparer.OrdinalIgnoreCase);
        if (!expanded.Add(path))
        {
            expanded.Remove(path);
        }

        var roots = ReconstructRoots(Rows);
        Rows = FolderTree.Flatten(roots, expanded);
    }

    public async Task LoadChildrenAsync(
        ExplorerWorkspace workspace,
        string path,
        CancellationToken cancellationToken = default)
    {
        if (await LoadChildrenAsync(workspace.FileOps, path, cancellationToken).ConfigureAwait(false))
        {
            workspace.NotifyChanged();
        }
    }

    public void ToggleExpanded(ExplorerWorkspace workspace, string path)
    {
        ToggleExpanded(path);
        workspace.NotifyChanged();
    }

    internal static List<TreeNode> MergeTree(IReadOnlyList<FolderTreeItem> rows, string parent, TreeNode[] children)
    {
        var roots = ReconstructRoots(rows);
        AttachChildren(roots, parent, children);
        return roots;
    }

    internal static List<TreeNode> ReconstructRoots(IReadOnlyList<FolderTreeItem> rows)
    {
        return rows
            .Where(row => row.Depth == 0)
            .Select(row => new TreeNode
            {
                Name = row.Name,
                Path = row.Path,
                HasChildren = row.HasChildren,
            })
            .ToList();
    }

    private static bool AttachChildren(IEnumerable<TreeNode> nodes, string parent, TreeNode[] children)
    {
        foreach (var node in nodes)
        {
            if (PathRules.PathsEqual(node.Path, parent))
            {
                node.Children = [.. children];
                node.HasChildren = children.Length > 0;
                return true;
            }

            if (AttachChildren(node.Children, parent, children))
            {
                return true;
            }
        }

        return false;
    }
}
