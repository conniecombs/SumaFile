using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleFile.PluginHost.Trust;

public enum ApprovalState { NeedsConsent, Enabled, Disabled }

public sealed record ApprovalRecord(
    string ManifestHash,
    SignatureStatus SignatureStatus,
    ApprovalState State,
    DateTimeOffset UpdatedAt,
    string? Folder = null);

/// <summary>Remembers which plugins the user enabled; re-prompts when a plugin's manifest or signature status changes.</summary>
public sealed class PluginApprovalStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public PluginApprovalStore(string path) => _path = path;

    /// <summary>Replaces the default store path when set (the UI smoke points it at its own disposable file).</summary>
    public const string PathVariable = "SUMAFILE_PLUGIN_APPROVALS_PATH";

    public static string DefaultPath() =>
        Environment.GetEnvironmentVariable(PathVariable) is { } overridePath && !string.IsNullOrWhiteSpace(overridePath)
            ? overridePath.Trim()
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SumaFile", "plugin-approvals.json");

    /// <summary>
    /// The stored decision for this plugin, or NeedsConsent when its manifest, signature status or folder
    /// changed since. The folder check stops a copy elsewhere (say, in another plugin root) from inheriting
    /// the approval of the one the user consented to.
    /// </summary>
    public ApprovalState Resolve(string pluginId, string manifestHash, SignatureStatus signature, string folder)
    {
        lock (_gate)
        {
            if (!Load().TryGetValue(pluginId, out var rec)) return ApprovalState.NeedsConsent;
            if (rec.ManifestHash != manifestHash || rec.SignatureStatus != signature) return ApprovalState.NeedsConsent;
            if (!SameFolder(rec.Folder, folder)) return ApprovalState.NeedsConsent;
            return rec.State;
        }
    }

    public void Set(string pluginId, string manifestHash, SignatureStatus signature, ApprovalState state, string folder)
    {
        lock (_gate)
        {
            var all = Load();
            all[pluginId] = new ApprovalRecord(manifestHash, signature, state, DateTimeOffset.UtcNow, NormalizeFolder(folder));
            Save(all);
        }
    }

    public void Remove(string pluginId)
    {
        lock (_gate)
        {
            var all = Load();
            if (all.Remove(pluginId)) Save(all);
        }
    }

    private static bool SameFolder(string? stored, string folder) =>
        !string.IsNullOrWhiteSpace(stored)
        && string.Equals(NormalizeFolder(stored), NormalizeFolder(folder), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeFolder(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private Dictionary<string, ApprovalRecord> Load()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, ApprovalRecord?>>(File.ReadAllText(_path), Json);
            // A null entry (hand-edited file) counts as no record.
            return loaded is null
                ? new()
                : loaded.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable file is treated as having no records, so every plugin
            // falls back to NeedsConsent rather than the host failing to start.
            return new();
        }
    }

    private void Save(Dictionary<string, ApprovalRecord> all)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
