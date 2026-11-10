using System.Text;
using SimpleFile.PluginHost.Manifest;
using SimpleFile.PluginHost.Trust;

namespace SimpleFile.PluginHost.Discovery;

public static class PluginDiscovery
{
    private const string ManifestFileName = "plugin.json";

    /// <summary>
    /// Semicolon-separated roots that replace the default when set (the UI smoke uses it). Only these overrides can
    /// name more than one root, so only they can shadow one plugin folder with another.
    /// </summary>
    public const string RootsVariable = "SUMAFILE_PLUGIN_ROOTS";

    /// <summary>
    /// A folder that replaces the plugin data root when set (every UI smoke mode uses it), so a smoke's plugin
    /// backends never write into the installation folder.
    /// </summary>
    public const string DataRootVariable = "SUMAFILE_PLUGIN_DATA_ROOT";

    /// <summary>A file that replaces <c>plugins.log</c> when set, so a smoke never writes into the installation folder.</summary>
    public const string LogPathVariable = "SUMAFILE_PLUGIN_LOG_PATH";

    /// <summary>
    /// The plugin roots: <see cref="InstallRoot"/> alone, which holds the bundled plugins and the ones the user
    /// installs (user requirement 2026-11-09: every plugin file stays in the installation folder), unless
    /// <see cref="RootsVariable"/> names others.
    /// </summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        var overrideRoots = OverrideRoots();
        if (overrideRoots.Length > 0) return overrideRoots;

        return [InstallRoot()];
    }

    /// <summary>
    /// The <c>plugins</c> folder next to the app: the release ships first-party plugins and <c>bundled.json</c>
    /// there, and Install from file unzips into it. Not affected by <see cref="RootsVariable"/>.
    /// </summary>
    public static string InstallRoot() => Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>
    /// The folder that holds each plugin backend's data folder, <c>&lt;root&gt;\&lt;id&gt;\</c>: the
    /// <see cref="DataRootVariable"/> override when set, else <c>plugins-data</c> next to the app.
    /// </summary>
    public static string DataRoot() =>
        Override(DataRootVariable) ?? Path.Combine(AppContext.BaseDirectory, "plugins-data");

    /// <summary>
    /// The plugin log, rotating to <c>plugins.log.1</c> at 1 MB: the <see cref="LogPathVariable"/> override when set,
    /// else <c>plugins.log</c> next to the app.
    /// </summary>
    public static string LogPath() =>
        Override(LogPathVariable) ?? Path.Combine(AppContext.BaseDirectory, "plugins.log");

    private static string? Override(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    /// <summary>
    /// One <c>[warn]</c> line for each plugin environment override in effect (<see cref="RootsVariable"/>,
    /// <see cref="PluginApprovalStore.PathVariable"/>, <see cref="DataRootVariable"/> and <see cref="LogPathVariable"/>),
    /// for <c>plugins.log</c>; empty when none is set.
    /// </summary>
    public static IReadOnlyList<string> DescribeOverrides()
    {
        var lines = new List<string>();
        var overrideRoots = OverrideRoots();
        if (overrideRoots.Length > 0)
        {
            lines.Add($"[warn] {RootsVariable} replaces the plugin roots: {string.Join(';', overrideRoots)}");
        }

        if (Override(PluginApprovalStore.PathVariable) is { } approvals)
        {
            lines.Add($"[warn] {PluginApprovalStore.PathVariable} replaces the plugin approvals file: {approvals}");
        }

        if (Override(DataRootVariable) is { } dataRoot)
        {
            lines.Add($"[warn] {DataRootVariable} replaces the plugin data folder: {dataRoot}");
        }

        if (Override(LogPathVariable) is { } logPath)
        {
            lines.Add($"[warn] {LogPathVariable} replaces the plugin log: {logPath}");
        }

        return lines;
    }

    private static string[] OverrideRoots() =>
        (Environment.GetEnvironmentVariable(RootsVariable) ?? string.Empty)
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static IReadOnlyList<DiscoveredPlugin> Scan(IReadOnlyList<string> roots)
    {
        var all = new List<DiscoveredPlugin>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;

            string[] folders;
            try { folders = Directory.GetDirectories(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var folder in folders)
            {
                // Dot-prefixed folders are the installer's staging, replaced and removing folders; they hold a real
                // plugin.json but can never match the id grammar, so they are not plugins.
                if (Path.GetFileName(folder).StartsWith('.')) continue;

                var manifestPath = Path.Combine(folder, ManifestFileName);
                if (!File.Exists(manifestPath)) continue;

                var id = Path.GetFileName(folder);
                // Read once: the same bytes are parsed, hashed and signature-checked, so the hash the trust decision
                // uses is the hash of the manifest that was read.
                byte[] bytes;
                try { bytes = File.ReadAllBytes(manifestPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    all.Add(new DiscoveredPlugin(id, folder, root, DiscoveryState.InvalidManifest, null, [ex.Message], null));
                    continue;
                }

                var hash = PluginPackageHash.ComputeHash(bytes);
                var read = PluginManifestReader.Read(DecodeManifest(bytes), id);
                var state = read.IsValid
                    ? DiscoveryState.Valid
                    : read.Errors.Any(e => e.Contains("sdkVersion", StringComparison.Ordinal))
                        ? DiscoveryState.IncompatibleSdk
                        : DiscoveryState.InvalidManifest;
                all.Add(new DiscoveredPlugin(id, folder, root, state, read.Manifest, read.Errors, null, hash, bytes));
            }
        }

        // Later root wins: shadow every earlier entry with the same id (only an override names more than one root).
        var liveById = new Dictionary<string, DiscoveredPlugin>(StringComparer.Ordinal);
        foreach (var p in all) liveById[p.Id] = p;

        return all
            .Select(p => ReferenceEquals(liveById[p.Id], p)
                ? p
                : p with { State = DiscoveryState.Shadowed, ShadowedByFolder = liveById[p.Id].Folder })
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ThenBy(p => p.State == DiscoveryState.Shadowed ? 0 : 1)
            .ToList();
    }

    /// <summary>The manifest bytes as UTF-8 text, without a leading byte order mark.</summary>
    private static string DecodeManifest(byte[] bytes)
    {
        var bom = Encoding.UTF8.Preamble;
        return bytes.AsSpan().StartsWith(bom) ? Encoding.UTF8.GetString(bytes.AsSpan(bom.Length)) : Encoding.UTF8.GetString(bytes);
    }
}
