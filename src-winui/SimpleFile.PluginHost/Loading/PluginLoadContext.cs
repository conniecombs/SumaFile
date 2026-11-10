using System.Reflection;
using System.Runtime.Loader;

namespace SimpleFile.PluginHost.Loading;

/// <summary>
/// One non-collectible load context per plugin folder. Host-shared assemblies (the SDK, WinUI and the rest of
/// the Windows App SDK, the BCL) resolve to the default context so their types unify; everything else comes from
/// the plugin folder. Native DLLs the plugin's deps.json lists load from the plugin folder too, unless the host
/// ships a DLL of that name, which then wins (spec §5.2).
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    // docs/PLUGINS.md "Host-shared assemblies" prints this list (PluginSharedAssembliesDocTests pins the two together).
    // Microsoft.Graphics is shared only for the two families the host ships, Display and Imaging: the bare prefix would
    // also capture Win2D (Microsoft.Graphics.Canvas*), which the host does not ship, so a plugin could not bundle it.
    private static readonly string[] SharedPrefixes =
    [
        "SimpleFile.PluginSdk",
        "Microsoft.WindowsAppSDK",
        "Microsoft.WindowsAppRuntime",
        "Microsoft.UI",
        "Microsoft.WinUI",
        "Microsoft.InteractiveExperiences",
        "Microsoft.Graphics.Display",
        "Microsoft.Graphics.Imaging",
        "Microsoft.Web.WebView2",
        "Microsoft.Security.Authentication.OAuth",
        "Microsoft.Windows",
        "WinRT",
        "CommunityToolkit.Mvvm",
        "System.",
        "Microsoft.Extensions.",
    ];

    /// <summary>The host-shared assembly name prefixes, in the order <see cref="IsHostShared"/> tries them.</summary>
    internal static IReadOnlyList<string> SharedPrefixList => SharedPrefixes;

    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _hostDirectory;

    public PluginLoadContext(string pluginFolder, string mainAssemblyPath)
        : this(pluginFolder, mainAssemblyPath, AppContext.BaseDirectory)
    {
    }

    /// <param name="hostDirectory">The host's own folder: a native DLL found there is never loaded from the plugin.</param>
    internal PluginLoadContext(string pluginFolder, string mainAssemblyPath, string hostDirectory)
        : base("plugin:" + Path.GetFileName(Path.TrimEndingDirectorySeparator(pluginFolder)), isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _hostDirectory = hostDirectory;
    }

    /// <summary>True when <paramref name="assemblyName"/> must come from the host (the default context).</summary>
    internal static bool IsHostShared(string assemblyName)
    {
        foreach (var prefix in SharedPrefixes)
        {
            if (assemblyName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null || IsHostShared(name))
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <summary>
    /// The plugin's copy of a native DLL its deps.json lists, or null when the host ships a DLL of that name (so
    /// the host's copy, which the host's own code already uses, wins) or the plugin lists none.
    /// </summary>
    internal string? ResolveUnmanagedPath(string unmanagedDllName)
    {
        if (HostShips(unmanagedDllName))
        {
            return null;
        }

        return _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
    }

    private bool HostShips(string unmanagedDllName)
    {
        try
        {
            return File.Exists(Path.Combine(_hostDirectory, unmanagedDllName))
                || File.Exists(Path.Combine(_hostDirectory, unmanagedDllName + ".dll"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A name Path.Combine or File.Exists rejects is never the host's. An absolute name is not caught here:
            // Path.Combine returns it unchanged, so it counts as the host's when that file exists.
            return false;
        }
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
        ResolveUnmanagedPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;

    internal IntPtr LoadUnmanagedForTest(string name) => LoadUnmanagedDll(name);
}
