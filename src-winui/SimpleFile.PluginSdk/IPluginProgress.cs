namespace SimpleFile.PluginSdk;

public interface IPluginProgress
{
    IPluginOperation Begin(string title);
}
