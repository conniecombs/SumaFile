using SimpleFile.Ipc;

namespace SimpleFile.Core;

internal sealed class FolderTreeController
{
    private List<TreeNode> _roots = [];
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);

    public List<FolderTreeItem> Rows { get; private set; } = [];

    public bool IsExpanded(string path) => _expanded.Contains(path);

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
            foreach (var row in Rows.Where(row => row.Expanded))
            {
                _expanded.Add(row.Path);
            }

            _expanded.Add(path);
            if (_roots.Count == 0 && Rows.Count == 0)
            {
                _roots = children.ToList();
            }
            else
            {
                if (_roots.Count == 0)
                {
                    _roots = ReconstructRoots(Rows);
                }

                if (!AttachChildren(_roots, path, children))
                {
                    _roots = children.ToList();
                }
            }

            Rows = FolderTree.Flatten(_roots, _expanded);
        }
        catch
        {
            // Tree is optional; listing still works.
        }

        return true;
    }

    public void ToggleExpanded(string path)
    {
        foreach (var row in Rows.Where(row => row.Expanded))
        {
            _expanded.Add(row.Path);
        }

        if (!_expanded.Add(path))
        {
            _expanded.Remove(path);
        }

        if (_roots.Count == 0 && Rows.Count > 0)
        {
            _roots = ReconstructRoots(Rows);
        }

        Rows = FolderTree.Flatten(_roots, _expanded);
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
        var roots = new List<TreeNode>();
        var stack = new Stack<(int Depth, TreeNode Node)>();

        foreach (var row in rows)
        {
            var node = new TreeNode
            {
                Name = row.Name,
                Path = row.Path,
                HasChildren = row.HasChildren,
                Children = [],
            };

            while (stack.Count > 0 && stack.Peek().Depth >= row.Depth)
            {
                stack.Pop();
            }

            if (stack.Count == 0)
            {
                roots.Add(node);
            }
            else
            {
                var parent = stack.Peek().Node;
                parent.Children.Add(node);
                parent.HasChildren = true;
            }

            stack.Push((row.Depth, node));
        }

        return roots;
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
