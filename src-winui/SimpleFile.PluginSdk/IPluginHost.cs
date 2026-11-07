namespace SimpleFile.PluginSdk;

public interface IPluginHost
{
    string PluginId { get; }
    Task<T> InvokeBackendAsync<T>(string method, object? args, CancellationToken ct);
    IDisposable OnBackendEvent<T>(string eventName, Action<T> handler);
    IPluginSettings Settings { get; }
    IPluginProgress Progress { get; }
    IPluginDialogs Dialogs { get; }
    IPluginWorkspace Workspace { get; }
    IPluginLogger Logger { get; }
}
