namespace SimpleFile.Core;

/// <summary>
/// One Settings window's plugin settings pages, keyed by plugin id and the plugin record's <c>Epoch</c>. A page is an
/// element with one parent, so each Settings window keeps its own cache and reuses a page only while the plugin's
/// runtime is the one that created it; any other epoch misses. Not thread-safe: the window uses it on its UI thread.
/// </summary>
public sealed class PluginPageCache<T> where T : class
{
    private readonly Dictionary<string, (int Epoch, T Page)> _pages = new(StringComparer.Ordinal);

    public bool TryGet(string pluginId, int epoch, out T? page)
    {
        if (_pages.TryGetValue(pluginId, out var entry) && entry.Epoch == epoch)
        {
            page = entry.Page;
            return true;
        }

        page = null;
        return false;
    }

    /// <summary>Stores the page of <paramref name="pluginId"/> for <paramref name="epoch"/>, replacing any other epoch of that plugin.</summary>
    public void Store(string pluginId, int epoch, T page)
    {
        ArgumentNullException.ThrowIfNull(pluginId);
        ArgumentNullException.ThrowIfNull(page);
        _pages[pluginId] = (epoch, page);
    }

    public void Remove(string pluginId) => _pages.Remove(pluginId);
}
