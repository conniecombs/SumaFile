namespace SimpleFile.PluginSdk;

/// <summary>
/// A read-only view of SumaFile's active pane, reached through <see cref="IPluginHost.Workspace"/>. The properties are
/// a snapshot the UI thread takes after each change, so they may be read from any thread.
/// </summary>
public interface IPluginWorkspace
{
    /// <summary>The active pane's folder, or null when there is none.</summary>
    string? ActivePath { get; }

    /// <summary>The full paths of the active pane's selected items, possibly empty.</summary>
    IReadOnlyList<string> SelectedPaths { get; }

    /// <summary>
    /// Raised on the UI thread when the active folder or the selection changes. An exception a handler throws is
    /// logged and does not reach SumaFile or the other plugins.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Opens <paramref name="path"/> in the active pane. Callable from any thread; the host always queues the navigation
    /// to the UI thread, so it never runs inside the caller.
    /// </summary>
    /// <param name="path">The folder to open.</param>
    /// <param name="ct">Cancels the navigation.</param>
    /// <returns>A task that completes once the folder is listed.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or whitespace.</exception>
    Task NavigateAsync(string path, CancellationToken ct);

    /// <summary>
    /// Lists the active folder again, for example after the plugin changed files in it. Callable from any thread; the
    /// host always queues the refresh to the UI thread, so it never runs inside the caller.
    /// </summary>
    /// <param name="ct">Cancels the refresh.</param>
    /// <returns>A task that completes once the folder is listed again.</returns>
    Task RefreshAsync(CancellationToken ct);
}
