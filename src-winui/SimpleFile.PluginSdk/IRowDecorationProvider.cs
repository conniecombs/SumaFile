namespace SimpleFile.PluginSdk;

/// <summary>
/// Supplies the per-row decorations and per-folder flags of <c>contributions.rowDecorations</c>. The plugin's entry
/// type implements it. The host asks once per folder sync, for every row of the listed folder in one batch.
/// </summary>
public interface IRowDecorationProvider
{
    /// <summary>
    /// Decorates one listed folder. Return <see cref="PluginFolderDecorations.Empty"/> when there is nothing to show.
    /// </summary>
    /// <remarks>
    /// Runs on a thread-pool thread, never on the UI thread, so it must not create or touch WinUI objects. The host
    /// waits a short budget for the result before it shows the folder, and applies a later result when it arrives.
    /// It cancels <paramref name="ct"/> when the user navigates on; honour it. An exception or a null result is logged
    /// and shows the folder undecorated; it does not fail the plugin.
    /// </remarks>
    Task<PluginFolderDecorations> GetDecorationsAsync(PluginDecorationRequest request, CancellationToken ct);
}
