using SimpleFile.PluginHost.Manifest;

namespace SimpleFile.PluginHost.Discovery;

public static class PluginDiscovery
{
    private const string ManifestFileName = "plugin.json";

    public static IReadOnlyList<string> DefaultRoots() =>
    [
        Path.Combine(AppContext.BaseDirectory, "plugins"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SumaFile", "plugins"),
    ];

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
                var manifestPath = Path.Combine(folder, ManifestFileName);
                if (!File.Exists(manifestPath)) continue;

                var id = Path.GetFileName(folder);
                string json;
                try { json = File.ReadAllText(manifestPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    all.Add(new DiscoveredPlugin(id, folder, root, DiscoveryState.InvalidManifest, null, [ex.Message], null));
                    continue;
                }

                var read = PluginManifestReader.Read(json, id);
                var state = read.IsValid
                    ? DiscoveryState.Valid
                    : read.Errors.Any(e => e.Contains("sdkVersion", StringComparison.Ordinal))
                        ? DiscoveryState.IncompatibleSdk
                        : DiscoveryState.InvalidManifest;
                all.Add(new DiscoveredPlugin(id, folder, root, state, read.Manifest, read.Errors, null));
            }
        }

        // Later root wins: shadow every earlier entry with the same id.
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
}
