using System.Collections.Concurrent;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginSdk;

namespace SimpleFile.PluginHost.Loading;

public sealed class PluginLoadException : Exception
{
    public PluginLoadException(string message) : base(message) { }
    public PluginLoadException(string message, Exception? inner) : base(message, inner) { }
}

public static class PluginActivator
{
    private static readonly ConcurrentDictionary<string, PluginLoadContext> Contexts = new(StringComparer.OrdinalIgnoreCase);

    public static IPlugin CreateInstance(DiscoveredPlugin plugin)
    {
        var ui = plugin.Manifest?.Ui
            ?? throw new PluginLoadException($"Plugin '{plugin.Id}' does not declare a UI assembly.");

        var assemblyPath = Path.GetFullPath(Path.Combine(plugin.Folder, ui.Assembly));
        if (!File.Exists(assemblyPath))
        {
            throw new PluginLoadException($"Plugin '{plugin.Id}' assembly not found: {assemblyPath}");
        }

        try
        {
            var context = Contexts.GetOrAdd(Path.GetFullPath(plugin.Folder), _ => new PluginLoadContext(plugin.Folder, assemblyPath));
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(ui.EntryType, throwOnError: false)
                ?? throw new PluginLoadException($"Plugin '{plugin.Id}' entry type '{ui.EntryType}' not found in {ui.Assembly}.");

            if (!typeof(IPlugin).IsAssignableFrom(type))
            {
                throw new PluginLoadException($"Plugin '{plugin.Id}' entry type '{ui.EntryType}' does not implement IPlugin.");
            }

            if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new PluginLoadException($"Plugin '{plugin.Id}' entry type '{ui.EntryType}' needs a public parameterless constructor.");
            }

            return (IPlugin)Activator.CreateInstance(type)!;
        }
        catch (PluginLoadException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PluginLoadException($"Plugin '{plugin.Id}' failed to load: {ex.Message}", ex);
        }
    }
}
