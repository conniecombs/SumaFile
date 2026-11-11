namespace SimpleFile.PluginHost;

/// <summary>Surfaces plugin progress operations in the host UI.</summary>
public interface IPluginProgressSink
{
    /// <summary>Starts an operation and returns its id. <paramref name="cancel"/> is called when the user cancels it.</summary>
    string Begin(string pluginId, string title, Action cancel);

    void Report(string operationId, long current, long total, string? detail);

    void Complete(string operationId);

    void Fail(string operationId, string message);
}

public interface IPluginNotifier
{
    void Notify(string pluginId, string message, bool isError);
}
