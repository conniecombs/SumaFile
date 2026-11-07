namespace SimpleFile.PluginSdk;

public interface IPluginLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}
