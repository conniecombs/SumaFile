namespace SimpleFile.PluginSdk;

public interface IPluginDialogs
{
    Task ShowMessageAsync(string title, string message, CancellationToken ct);
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel, CancellationToken ct);
    Task<string?> PickFolderAsync(CancellationToken ct);
}
