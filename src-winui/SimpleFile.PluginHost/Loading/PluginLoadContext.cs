using System.Reflection;
using System.Runtime.Loader;

namespace SimpleFile.PluginHost.Loading;

/// <summary>
/// One non-collectible load context per plugin folder. Host-shared assemblies (the SDK, WinUI,
/// the BCL) resolve to the default context so their types unify; everything else comes from the plugin folder.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string[] SharedPrefixes =
    [
        "SimpleFile.PluginSdk",
        "Microsoft.WindowsAppSDK",
        "Microsoft.UI",
        "Microsoft.Windows",
        "WinRT",
        "CommunityToolkit.Mvvm",
        "System.",
        "Microsoft.Extensions.",
    ];

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginFolder, string mainAssemblyPath)
        : base("plugin:" + Path.GetFileName(Path.TrimEndingDirectorySeparator(pluginFolder)), isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null)
        {
            return null;
        }

        foreach (var prefix in SharedPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
