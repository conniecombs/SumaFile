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

/// <summary>
/// The approvals file as one read saw it: its records, and whether it exists but could not be read (then the
/// records are empty). A scan judges every plugin against one snapshot, so it reads the file once.
/// </summary>
internal sealed record ApprovalSnapshot(IReadOnlyDictionary<string, ApprovalRecord> Records, bool Unreadable);

/// <summary>The approvals file format: PascalCase keys, string enums, indented.</summary>
[JsonSourceGenerationOptions(UseStringEnumConverter = true, WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, ApprovalRecord>))]
internal partial class PluginApprovalJsonContext : JsonSerializerContext;

/// <summary>Remembers which plugins the user enabled; re-prompts when a plugin's manifest or signature status changes.</summary>
public sealed class PluginApprovalStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public PluginApprovalStore(string path) => _path = path;

    /// <summary>Replaces the default store path when set (the UI smoke points it at its own disposable file).</summary>
    public const string PathVariable = "SUMAFILE_PLUGIN_APPROVALS_PATH";

    /// <summary>
    /// The <see cref="PathVariable"/> override when set, else <c>plugin-approvals.json</c> next to the app: like every
    /// plugin file, the approvals stay in the installation folder.
    /// </summary>
    public static string DefaultPath() =>
        Environment.GetEnvironmentVariable(PathVariable) is { } overridePath && !string.IsNullOrWhiteSpace(overridePath)
            ? overridePath.Trim()
            : Path.Combine(AppContext.BaseDirectory, "plugin-approvals.json");

    /// <summary>
    /// The stored decision for this plugin, or NeedsConsent when its manifest, signature status or folder
    /// changed since. The folder check stops a copy elsewhere (say, in another plugin root) from inheriting
    /// the approval of the one the user consented to. An unreadable file counts as empty.
    /// </summary>
    public ApprovalState Resolve(string pluginId, string manifestHash, SignatureStatus signature, string folder) =>
        Resolve(pluginId, manifestHash, signature, folder, Snapshot());

    /// <summary><see cref="Resolve(string, string, SignatureStatus, string)"/> against an earlier <see cref="Snapshot"/>.</summary>
    internal ApprovalState Resolve(
        string pluginId, string manifestHash, SignatureStatus signature, string folder, ApprovalSnapshot snapshot)
    {
        if (!snapshot.Records.TryGetValue(pluginId, out var rec)) return ApprovalState.NeedsConsent;
        if (rec.ManifestHash != manifestHash || rec.SignatureStatus != signature) return ApprovalState.NeedsConsent;
        if (!SameFolder(rec.Folder, folder)) return ApprovalState.NeedsConsent;
        return rec.State;
    }

    /// <summary>Reads the file once; an unreadable file is an empty snapshot marked <see cref="ApprovalSnapshot.Unreadable"/>.</summary>
    internal ApprovalSnapshot Snapshot()
    {
        lock (_gate)
        {
            var (records, readError) = Load();
            return new ApprovalSnapshot(records, readError is not null);
        }
    }

    /// <summary>
    /// The state stored for this plugin in this folder whatever manifest hash or signature status it was stored
    /// with; null when there is none (or the file cannot be read). Bundled trust uses it so a user's Disabled
    /// survives an upgrade that changes the manifest.
    /// </summary>
    public ApprovalState? StoredState(string pluginId, string folder) => StoredState(pluginId, folder, out _);

    /// <summary>
    /// <see cref="StoredState(string, string)"/>, also reporting whether the file exists but could not be read
    /// even after the retries. Then the result is null whatever the file holds, so a caller that would turn a
    /// plugin on when nothing is stored (bundled trust) must fail safe instead.
    /// </summary>
    public ApprovalState? StoredState(string pluginId, string folder, out bool unreadable) =>
        StoredState(pluginId, folder, Snapshot(), out unreadable);

    /// <summary><see cref="StoredState(string, string, out bool)"/> against an earlier <see cref="Snapshot"/>.</summary>
    internal ApprovalState? StoredState(string pluginId, string folder, ApprovalSnapshot snapshot, out bool unreadable)
    {
        unreadable = snapshot.Unreadable;
        return snapshot.Records.TryGetValue(pluginId, out var rec) && SameFolder(rec.Folder, folder)
            ? rec.State
            : null;
    }

    /// <exception cref="IOException">The file exists but could not be read; nothing is saved, so the other
    /// plugins' records are never overwritten with an empty store.</exception>
    public void Set(string pluginId, string manifestHash, SignatureStatus signature, ApprovalState state, string folder)
    {
        lock (_gate)
        {
            var all = LoadForWrite();
            all[pluginId] = new ApprovalRecord(manifestHash, signature, state, DateTimeOffset.UtcNow, NormalizeFolder(folder));
            Save(all);
        }
    }

    /// <exception cref="IOException">The file exists but could not be read; nothing is saved.</exception>
    public void Remove(string pluginId)
    {
        lock (_gate)
        {
            var all = LoadForWrite();
            if (all.Remove(pluginId)) Save(all);
        }
    }

    private const int IoRetries = 3;
    private static readonly TimeSpan IoRetryDelay = TimeSpan.FromMilliseconds(50);

    private static bool SameFolder(string? stored, string folder) =>
        !string.IsNullOrWhiteSpace(stored)
        && string.Equals(NormalizeFolder(stored), NormalizeFolder(folder), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeFolder(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private Dictionary<string, ApprovalRecord> LoadForWrite()
    {
        var (records, readError) = Load();
        if (readError is not null)
        {
            throw new IOException($"Could not read the plugin approvals file: {readError.Message}", readError);
        }

        return records;
    }

    private int _loadCount;

    /// <summary>How many times this store has read its file (tests).</summary>
    internal int LoadCount => Volatile.Read(ref _loadCount);

    /// <summary>
    /// The stored records, and the error when the file exists but could not be read even after the retries
    /// (another SumaFile or the antivirus may hold it for a moment). A missing file, or one that is not valid
    /// JSON, is an empty store with no error, so every plugin falls back to NeedsConsent rather than the host
    /// failing to start, and the next Set replaces the corrupt file.
    /// </summary>
    private (Dictionary<string, ApprovalRecord> Records, Exception? ReadError) Load()
    {
        Interlocked.Increment(ref _loadCount);
        for (var attempt = 0; ; attempt++)
        {
            string text;
            try
            {
                if (!File.Exists(_path)) return (new(), null);
                // Delete sharing, so a Save from another store can replace the file while this one reads it.
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                return (new(), null);
            }
            catch (DirectoryNotFoundException)
            {
                return (new(), null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= IoRetries) return (new(), e);
                Thread.Sleep(IoRetryDelay);
                continue;
            }

            try
            {
                var loaded = JsonSerializer.Deserialize(text, PluginApprovalJsonContext.Default.DictionaryStringApprovalRecord);
                // A null entry (hand-edited file) counts as no record.
                return loaded is null
                    ? (new(), null)
                    : (loaded.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value), null);
            }
            catch (JsonException)
            {
                return (new(), null);
            }
        }
    }

    private void Save(Dictionary<string, ApprovalRecord> all)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Unique per process and call: two stores (or two SumaFile processes) never write the same temp file.
        var tmp = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, PluginApprovalJsonContext.Default.DictionaryStringApprovalRecord));
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, _path, overwrite: true);
                    return;
                }
                catch (Exception e) when (attempt < IoRetries && e is IOException or UnauthorizedAccessException)
                {
                    // A reader without delete sharing (another process, the antivirus) holds the file briefly.
                    Thread.Sleep(IoRetryDelay);
                }
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
