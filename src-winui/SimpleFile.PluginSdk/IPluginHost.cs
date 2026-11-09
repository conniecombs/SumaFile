namespace SimpleFile.PluginSdk;

/// <summary>The host services a plugin receives in <see cref="IPlugin.ActivateAsync"/> and keeps for its lifetime.</summary>
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

    /// <summary>Hands plugin-created windows to the host, which closes them before it stops the plugin.</summary>
    IPluginWindows Windows { get; }

    /// <summary>Shows and hides the plugin's <c>contributions.panels</c>; callable from any thread.</summary>
    IPluginPanels Panels { get; }

    /// <summary>Asks the plugin's row decoration provider again; callable from any thread.</summary>
    IPluginDecorations Decorations { get; }
}
