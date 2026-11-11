namespace SimpleFile.Core;

/// <summary>One plugin's decoration of one row: the status text, its theme brush key and a tooltip.</summary>
/// <param name="PluginId">The plugin that decorated the row.</param>
/// <param name="Text">The text the status column and the row's secondary text show, as the plugin gave it.</param>
/// <param name="BrushKey">A theme brush key, checked against <see cref="DecorationBrushKeys.Allowed"/> when drawn.</param>
/// <param name="Tooltip">An optional tooltip.</param>
public sealed record RowDecorationEntry(string PluginId, string Text, string? BrushKey, string? Tooltip);

/// <summary>One plugin's answer for one folder.</summary>
/// <param name="PluginId">The plugin that answered.</param>
/// <param name="Rows">Decorations by row path. Keys are matched through <see cref="RowDecorationKeys"/>.</param>
/// <param name="FolderFlags">Folder flags, already <c>&lt;pluginId&gt;:&lt;flag&gt;</c>, such as <c>com.sumafile.git:inRepository</c>.</param>
public sealed record FolderDecorationBatch(
    string PluginId,
    IReadOnlyDictionary<string, RowDecorationEntry> Rows,
    IReadOnlyList<string> FolderFlags);

/// <summary>
/// Where the <see cref="RowDecorationCoordinator"/> gets decorations: the App's bridge to the enabled plugins'
/// row decoration providers. The coordinator calls it only from the thread pool.
/// </summary>
public interface IRowDecorationSource
{
    /// <summary>The decorating plugins, in precedence order: the first one with a row's decoration owns its text.</summary>
    IReadOnlyList<string> PluginIds { get; }

    /// <summary>
    /// Asks one plugin for the decorations of <paramref name="paths"/>, the rows listed in <paramref name="folderPath"/>.
    /// Null, a throw or a cancellation means the plugin has nothing for this folder.
    /// </summary>
    Task<FolderDecorationBatch?> DecorateAsync(
        string pluginId,
        string folderPath,
        IReadOnlyList<string> paths,
        CancellationToken ct);
}

/// <summary>
/// The one rule that matches a decoration's key to a listed row, shared by the host's key filter and the row lookup:
/// separators become <c>\</c>, a trailing separator is dropped unless the path is a drive root, and the comparison
/// ignores case. So git's <c>R:/repo/Dir/A.TXT</c> and <c>R:\repo\untracked\</c> land on the rows
/// <c>R:\repo\dir\a.txt</c> and <c>R:\repo\untracked</c>.
/// </summary>
public static class RowDecorationKeys
{
    /// <summary>The path in the form keys are compared in.</summary>
    public static string Normalize(string path)
    {
        var separated = (path ?? "").Replace('/', '\\');
        if (PathRules.IsWindowsRoot(separated))
        {
            var root = separated.Trim();
            return $"{root[..2]}\\";
        }

        var trimmed = separated.TrimEnd('\\');
        return trimmed.Length == 0 && separated.Length > 0 ? "\\" : trimmed;
    }

    /// <summary>Equality and ordering over <see cref="Normalize"/>, ignoring case.</summary>
    public static StringComparer Comparer { get; } = new NormalizedComparer();

    private sealed class NormalizedComparer : StringComparer
    {
        public override int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            return y is null ? 1 : StringComparer.OrdinalIgnoreCase.Compare(Normalize(x), Normalize(y));
        }

        public override bool Equals(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            return x is not null
                && y is not null
                && StringComparer.OrdinalIgnoreCase.Equals(Normalize(x), Normalize(y));
        }

        public override int GetHashCode(string obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            return StringComparer.OrdinalIgnoreCase.GetHashCode(Normalize(obj));
        }
    }
}

/// <summary>
/// The decorations of the folder a pane shows: the merge of every decorating plugin's batch, in precedence order.
/// Immutable; a pane swaps in a new instance.
/// </summary>
public sealed class PaneDecorations
{
    private static readonly IReadOnlySet<string> NoFlags = new HashSet<string>(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> NoPlugins = new HashSet<string>(StringComparer.Ordinal);

    private readonly Dictionary<string, RowDecorationEntry> _first;
    private readonly Dictionary<string, List<string>> _plugins;
    private readonly string[] _order;
    private readonly FolderDecorationBatch[] _batches;
    private readonly IReadOnlySet<string> _pending;

    private PaneDecorations(
        string? folderPath,
        Dictionary<string, RowDecorationEntry> first,
        Dictionary<string, List<string>> plugins,
        IReadOnlySet<string> folderFlags,
        string[] order,
        FolderDecorationBatch[] batches,
        IReadOnlySet<string> pending)
    {
        FolderPath = folderPath;
        _first = first;
        _plugins = plugins;
        FolderFlags = folderFlags;
        _order = order;
        _batches = batches;
        _pending = pending;
    }

    /// <summary>No decorations: before startup is ready, without providers, and for skipped locations.</summary>
    public static PaneDecorations Empty { get; } = new(
        null,
        new Dictionary<string, RowDecorationEntry>(RowDecorationKeys.Comparer),
        new Dictionary<string, List<string>>(RowDecorationKeys.Comparer),
        NoFlags,
        [],
        [],
        NoPlugins);

    /// <summary>The folder these decorations belong to, or null for <see cref="Empty"/>.</summary>
    public string? FolderPath { get; }

    /// <summary>Every plugin's folder flags, <c>&lt;pluginId&gt;:&lt;flag&gt;</c>, for <c>when</c> expressions.</summary>
    public IReadOnlySet<string> FolderFlags { get; }

    /// <summary>
    /// False for a result the budget cut short: a plugin had not answered yet, and its answer arrives as a later merge.
    /// </summary>
    internal bool IsComplete => _pending.Count == 0;

    internal bool HasNoDecorations => _first.Count == 0 && _plugins.Count == 0 && FolderFlags.Count == 0;

    /// <summary>The row's decoration from the first plugin in precedence order, or null.</summary>
    public RowDecorationEntry? For(string path) =>
        path is not null && _first.TryGetValue(path, out var entry) ? entry : null;

    /// <summary>Every plugin that decorates at least one of <paramref name="paths"/>.</summary>
    public IReadOnlySet<string> PluginsFor(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (_plugins.Count == 0)
        {
            return NoPlugins;
        }

        var plugins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (path is not null && _plugins.TryGetValue(path, out var decorating))
            {
                plugins.UnionWith(decorating);
            }
        }

        return plugins;
    }

    /// <summary>
    /// Merges the batches of the plugins in <paramref name="pluginOrder"/>. The first plugin in that order with a row's
    /// decoration owns its text; every plugin with one counts for <see cref="PluginsFor"/>. A batch from a plugin outside
    /// the order is ignored.
    /// </summary>
    public static PaneDecorations Merge(
        string folderPath,
        IReadOnlyList<string> pluginOrder,
        IEnumerable<FolderDecorationBatch> batches) =>
        Merge(folderPath, pluginOrder, batches, pendingPlugins: null);

    /// <summary>
    /// The merge for an incomplete result, with <paramref name="pendingPlugins"/> the plugins still answering. Without
    /// pending plugins the result is complete.
    /// </summary>
    internal static PaneDecorations Merge(
        string folderPath,
        IReadOnlyList<string> pluginOrder,
        IEnumerable<FolderDecorationBatch> batches,
        IEnumerable<string>? pendingPlugins)
    {
        ArgumentNullException.ThrowIfNull(pluginOrder);
        ArgumentNullException.ThrowIfNull(batches);
        var order = pluginOrder.Distinct(StringComparer.Ordinal).ToArray();
        var pending = new HashSet<string>(
            (pendingPlugins ?? []).Where(pluginId => order.Contains(pluginId, StringComparer.Ordinal)),
            StringComparer.Ordinal);
        var byPlugin = new Dictionary<string, FolderDecorationBatch>(StringComparer.Ordinal);
        foreach (var batch in batches)
        {
            if (batch is not null)
            {
                byPlugin.TryAdd(batch.PluginId, batch);
            }
        }

        var first = new Dictionary<string, RowDecorationEntry>(RowDecorationKeys.Comparer);
        var plugins = new Dictionary<string, List<string>>(RowDecorationKeys.Comparer);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var used = new List<FolderDecorationBatch>();
        foreach (var pluginId in order)
        {
            if (!byPlugin.TryGetValue(pluginId, out var batch))
            {
                continue;
            }

            used.Add(batch);

            foreach (var (path, entry) in batch.Rows)
            {
                if (string.IsNullOrEmpty(path) || entry is null)
                {
                    continue;
                }

                first.TryAdd(path, entry with { PluginId = pluginId });
                if (!plugins.TryGetValue(path, out var decorating))
                {
                    decorating = [];
                    plugins[path] = decorating;
                }

                if (!decorating.Contains(pluginId, StringComparer.Ordinal))
                {
                    decorating.Add(pluginId);
                }
            }

            flags.UnionWith(batch.FolderFlags);
        }

        return new PaneDecorations(folderPath, first, plugins, flags, order, [.. used], pending);
    }

    /// <summary>
    /// For an incomplete result of the folder <paramref name="shown"/> belongs to: the plugins that answered (or
    /// answered nothing) replace their part, each plugin still pending keeps its part of <paramref name="shown"/>, and a
    /// plugin that left the set is dropped. A complete result, or one for another folder, is returned as is.
    /// </summary>
    internal PaneDecorations KeepingPendingFrom(PaneDecorations shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        if (IsComplete
            || FolderPath is null
            || shown.FolderPath is null
            || !PathRules.PathsEqual(FolderPath, shown.FolderPath))
        {
            return this;
        }

        var kept = shown._batches.Where(batch => _pending.Contains(batch.PluginId));
        return Merge(FolderPath, _order, _batches.Concat(kept), _pending);
    }

    /// <summary>True when both hold the same folder and the very same plugin answers, in the same order.</summary>
    internal bool HasSameContentAs(PaneDecorations other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(FolderPath, other.FolderPath, StringComparison.Ordinal)
            && _batches.Length == other._batches.Length
            && _batches.Zip(other._batches).All(pair => ReferenceEquals(pair.First, pair.Second));
    }
}
