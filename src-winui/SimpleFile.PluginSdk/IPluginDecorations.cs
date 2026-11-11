namespace SimpleFile.PluginSdk;

/// <summary>The plugin's row decorations, reached through <see cref="IPluginHost.Decorations"/>.</summary>
public interface IPluginDecorations
{
    /// <summary>
    /// Asks this plugin's <see cref="IRowDecorationProvider"/> again for the folders on screen, for example after the
    /// plugin changed what it decorates. Callable from any thread; returns at once, and the host runs the requests on
    /// the thread pool.
    /// </summary>
    void Invalidate();
}
