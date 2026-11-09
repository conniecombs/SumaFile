namespace SimpleFile.PluginSdk;

public interface IPluginWorkspace
{
    string? ActivePath { get; }
    IReadOnlyList<string> SelectedPaths { get; }
    event EventHandler? Changed;
    Task NavigateAsync(string path, CancellationToken ct);

    /// <summary>
    /// Lists the active folder again, for example after the plugin changed files in it. Callable from any thread; the
    /// host always queues the refresh to the UI thread, so it never runs inside the caller.
    /// </summary>
    Task RefreshAsync(CancellationToken ct);
}
