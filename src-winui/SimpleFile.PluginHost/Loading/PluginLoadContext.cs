using System.Reflection;
using System.Runtime.Loader;

namespace SimpleFile.PluginHost.Loading;

/// <summary>
/// One non-collectible load context per plugin folder. Host-only assemblies (the SDK, WinUI and the rest of the
/// Windows App SDK, the Windows SDK projection, WinRT, the MVVM Toolkit) always resolve to the default context so their
/// types unify. Host-first ones (the BCL's <c>System.</c> and <c>Microsoft.Extensions.</c>) resolve to the default
/// context when it can provide them, and otherwise from the plugin folder, so a plugin can bundle System.Reactive or a
/// Microsoft.Extensions package the host does not ship. Everything else comes from the plugin folder. Native DLLs the
/// plugin's deps.json lists load from the plugin folder too, unless the host ships a DLL of that name, which then wins
/// (spec §5.2).
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    /// <summary>Where an assembly name resolves from (<see cref="Classify"/>).</summary>
    internal enum SharedKind
    {
        /// <summary>Not host-shared: from the plugin folder.</summary>
        None,

        /// <summary>Always the host's copy; a plugin never loads its own.</summary>
        HostOnly,

        /// <summary>The host's copy when the default context can provide it (<see cref="HostProvides"/>), else the plugin's.</summary>
        HostFirst,
    }

    // docs/PLUGINS.md "Host-shared assemblies" prints this list (PluginSharedAssembliesDocTests pins the two together).
    // Microsoft.Graphics is shared only for the two families the host ships, Display and Imaging: the bare prefix would
    // also capture Win2D (Microsoft.Graphics.Canvas*), which the host does not ship, so a plugin could not bundle it.
    // Microsoft.Windows. keeps its dot for the same reason: without it the prefix captured Microsoft.WindowsAzure.*.
    private static readonly (string Prefix, SharedKind Kind)[] SharedPrefixes =
    [
        ("SimpleFile.PluginSdk", SharedKind.HostOnly),
        ("Microsoft.WindowsAppSDK", SharedKind.HostOnly),
        ("Microsoft.WindowsAppRuntime", SharedKind.HostOnly),
        ("Microsoft.UI", SharedKind.HostOnly),
        ("Microsoft.WinUI", SharedKind.HostOnly),
        ("Microsoft.InteractiveExperiences", SharedKind.HostOnly),
        ("Microsoft.Graphics.Display", SharedKind.HostOnly),
        ("Microsoft.Graphics.Imaging", SharedKind.HostOnly),
        ("Microsoft.Web.WebView2", SharedKind.HostOnly),
        ("Microsoft.Security.Authentication.OAuth", SharedKind.HostOnly),
        ("Microsoft.Windows.", SharedKind.HostOnly),
        ("WinRT", SharedKind.HostOnly),
        ("CommunityToolkit.Mvvm", SharedKind.HostOnly),
        ("System.", SharedKind.HostFirst),
        ("Microsoft.Extensions.", SharedKind.HostFirst),
    ];

    /// <summary>The host-shared assembly name prefixes, in the order <see cref="Classify"/> tries them.</summary>
    internal static IReadOnlyList<string> SharedPrefixList { get; } = SharedPrefixes.Select(entry => entry.Prefix).ToArray();

    // The simple names the default context can load: the trusted platform assemblies (the runtime and, for a
    // self-contained app such as SumaFile, every assembly it ships). Read once, on the first host-first lookup.
    private static readonly Lazy<HashSet<string>> TrustedPlatformAssemblies = new(ReadTrustedPlatformAssemblies);

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

    /// <summary>Whether <paramref name="assemblyName"/> is host-only, host-first or the plugin's (the first matching prefix).</summary>
    internal static SharedKind Classify(string assemblyName)
    {
        foreach (var (prefix, kind) in SharedPrefixes)
        {
            if (assemblyName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        return SharedKind.None;
    }

    /// <summary>True when <paramref name="assemblyName"/> may come from the host: host-only or host-first.</summary>
    internal static bool IsHostShared(string assemblyName) => Classify(assemblyName) != SharedKind.None;

    /// <summary>
    /// The default context can provide an assembly of this simple name: it is a trusted platform assembly, or one
    /// already loaded there. Case-insensitive, as assembly names are.
    /// </summary>
    internal static bool HostProvides(string assemblyName) =>
        TrustedPlatformAssemblies.Value.Contains(assemblyName)
        || Default.Assemblies.Any(assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> ReadTrustedPlatformAssemblies()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string list)
        {
            foreach (var path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                names.Add(Path.GetFileNameWithoutExtension(path));
            }
        }

        return names;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null)
        {
            return null;
        }

        switch (Classify(name))
        {
            case SharedKind.HostOnly:
                return null;
            case SharedKind.HostFirst when HostProvides(name):
                return null;
        }

        // Not shared, or host-first but the host has no copy: the plugin's own, when its deps.json lists one. Null
        // falls back to the default context, which fails a name it cannot provide.
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
