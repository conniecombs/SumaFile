using SimpleFile.Core;
using SimpleFile.PluginSdk;

namespace SimpleFile.PluginHost.Contributions;

/// <summary>
/// The App's bridge from the <see cref="RowDecorationCoordinator"/> to the enabled plugins' row decoration providers.
/// Each call goes through <see cref="PluginManager.GetRowDecorationsAsync"/>, which activates the plugin on the UI
/// thread and runs its provider on the thread pool. The answer becomes a <see cref="FolderDecorationBatch"/>: rows map
/// to <see cref="RowDecorationEntry"/>, folder flags become <c>&lt;pluginId&gt;:&lt;flag&gt;</c>, and a null result
/// stays null.
/// </summary>
/// <remarks>
/// The key filter keeps a returned key only when it matches a requested path under <see cref="RowDecorationKeys.Comparer"/>,
/// the rule <see cref="PaneDecorations.For"/> uses, and stores the entry under the requested path's spelling. So git's
/// <c>R:/repo/Dir/A.TXT</c> or <c>R:\repo\untracked\</c> reaches the row <c>R:\repo\dir\a.txt</c> or
/// <c>R:\repo\untracked</c>. When two returned keys map to one requested path, the first wins.
/// </remarks>
public sealed class PluginRowDecorationSource : IRowDecorationSource
{
    private readonly PluginManager _manager;
    private readonly Dictionary<string, string> _providers = new(StringComparer.Ordinal);

    /// <param name="manager">The plugin manager that owns the providers.</param>
    /// <param name="providers">
    /// The enabled decorating plugins in precedence order, as <see cref="PluginContributionAdapter.RowDecorationProviders"/>
    /// lists them.
    /// </param>
    public PluginRowDecorationSource(PluginManager manager, IReadOnlyList<PluginDecorationProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(providers);
        _manager = manager;
        var ids = new List<string>(providers.Count);
        foreach (var provider in providers)
        {
            if (provider is not null && _providers.TryAdd(provider.PluginId, provider.Provider))
            {
                ids.Add(provider.PluginId);
            }
        }

        PluginIds = ids;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> PluginIds { get; }

    /// <inheritdoc />
    public async Task<FolderDecorationBatch?> DecorateAsync(
        string pluginId,
        string folderPath,
        IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (pluginId is null || !_providers.TryGetValue(pluginId, out var provider))
        {
            return null;
        }

        var decorations = await _manager
            .GetRowDecorationsAsync(pluginId, new PluginDecorationRequest(provider, folderPath, paths), ct)
            .ConfigureAwait(false);
        return decorations is null ? null : ToBatch(pluginId, paths, decorations);
    }

    private static FolderDecorationBatch ToBatch(string pluginId, IReadOnlyList<string> paths, PluginFolderDecorations decorations)
    {
        var rows = new Dictionary<string, RowDecorationEntry>(StringComparer.Ordinal);
        if (decorations.Rows is { Count: > 0 } returned)
        {
            // Requested spelling by normalized key: the first spelling of a path wins, like the listing's own order.
            var requested = new Dictionary<string, string>(RowDecorationKeys.Comparer);
            foreach (var path in paths)
            {
                if (!string.IsNullOrEmpty(path))
                {
                    requested.TryAdd(path, path);
                }
            }

            foreach (var (key, decoration) in returned)
            {
                if (string.IsNullOrEmpty(key)
                    || decoration is null
                    || decoration.Text is null
                    || !requested.TryGetValue(key, out var spelling))
                {
                    continue;
                }

                rows.TryAdd(spelling, new RowDecorationEntry(pluginId, decoration.Text, decoration.BrushKey, decoration.Tooltip));
            }
        }

        var flags = new List<string>();
        foreach (var flag in decorations.FolderFlags ?? [])
        {
            if (!string.IsNullOrWhiteSpace(flag))
            {
                // The coordinator drops a flag outside the grammar after this prefix.
                flags.Add($"{pluginId}:{flag}");
            }
        }

        return new FolderDecorationBatch(pluginId, rows, flags);
    }
}
