using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleFile.PluginHost.Trust;

public enum ApprovalState { NeedsConsent, Enabled, Disabled }

public sealed record ApprovalRecord(
    string ManifestHash,
    SignatureStatus SignatureStatus,
    ApprovalState State,
    DateTimeOffset UpdatedAt);

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

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SumaFile", "plugin-approvals.json");

    public ApprovalState Resolve(string pluginId, string manifestHash, SignatureStatus signature)
    {
        lock (_gate)
        {
            if (!Load().TryGetValue(pluginId, out var rec)) return ApprovalState.NeedsConsent;
            if (rec.ManifestHash != manifestHash || rec.SignatureStatus != signature) return ApprovalState.NeedsConsent;
            return rec.State;
        }
    }

    public void Set(string pluginId, string manifestHash, SignatureStatus signature, ApprovalState state)
    {
        lock (_gate)
        {
            var all = Load();
            all[pluginId] = new ApprovalRecord(manifestHash, signature, state, DateTimeOffset.UtcNow);
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

    private Dictionary<string, ApprovalRecord> Load()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ApprovalRecord>>(File.ReadAllText(_path), Json)
                   ?? new();
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
