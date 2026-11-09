using System.Text.Json;
using SimpleFile.PluginHost.Manifest;

namespace SimpleFile.PluginHost.Trust;

/// <summary>One first-party plugin the release build bundled, with the SHA-256 of the manifest it shipped.</summary>
public sealed record BundledPlugin(string Id, string ManifestSha256);

/// <summary>
/// Reads <c>&lt;install root&gt;\bundled.json</c>, which the release build writes next to the plugins it ships:
/// <c>{"plugins":[{"id":"com.sumafile.sevenzip","manifestSha256":"&lt;64 lowercase hex&gt;"}]}</c>.
/// A plugin it lists, found in that same root with that manifest hash, is trusted without consent. The release
/// build writes UTF-8 without a BOM; a leading BOM is skipped all the same.
/// </summary>
public static class BundledPluginList
{
    public const string FileName = "bundled.json";

    // Far above any real list; a larger file is not one the release build wrote.
    private const long MaxFileBytes = 1_048_576;
    private const int HashLength = 64;
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// The listed plugins as id to lowercase manifest hash. Empty when the file is missing, unreadable or not
    /// the expected shape; an entry without a valid id or a 64-digit hex hash is skipped, and for a repeated id
    /// the first entry wins.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Read(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var plugin in ReadEntries(root))
        {
            result.TryAdd(plugin.Id, plugin.ManifestSha256);
        }

        return result;
    }

    private static List<BundledPlugin> ReadEntries(string root)
    {
        var entries = new List<BundledPlugin>();
        byte[] bytes;
        try
        {
            var path = Path.Combine(root, FileName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return entries;
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return entries;
        }

        // A list saved by an editor or Windows PowerShell may start with a UTF-8 BOM, which JsonDocument rejects.
        ReadOnlyMemory<byte> json = bytes;
        if (bytes.AsSpan().StartsWith(Utf8Bom)) json = json[Utf8Bom.Length..];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("plugins", out var plugins)
                || plugins.ValueKind != JsonValueKind.Array)
            {
                return entries;
            }

            foreach (var item in plugins.EnumerateArray())
            {
                if (TryReadEntry(item) is { } entry) entries.Add(entry);
            }
        }
        catch (JsonException)
        {
            // Malformed JSON: no list at all, so nothing is trusted by it.
            entries.Clear();
        }

        return entries;
    }

    private static BundledPlugin? TryReadEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return null;
        if (!item.TryGetProperty("manifestSha256", out var hash) || hash.ValueKind != JsonValueKind.String) return null;

        var idText = id.GetString()!;
        var hashText = hash.GetString()!;
        if (!PluginManifestReader.IsValidId(idText) || !IsSha256Hex(hashText)) return null;
        return new BundledPlugin(idText, hashText.ToLowerInvariant());
    }

    private static bool IsSha256Hex(string value) =>
        value.Length == HashLength && value.All(char.IsAsciiHexDigit);
}
