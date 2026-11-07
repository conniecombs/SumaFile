namespace SimpleFile.PluginSdk;

public interface IPluginWorkspace
{
    string? ActivePath { get; }
    IReadOnlyList<string> SelectedPaths { get; }
    event EventHandler? Changed;
    Task NavigateAsync(string path, CancellationToken ct);
}
