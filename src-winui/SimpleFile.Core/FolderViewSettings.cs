using System.Text.Json;
using System.Text.Json.Serialization;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Core;

public enum FolderViewScope
{
    Global,
    Folder,
    Descendants,
}

public sealed class FolderViewSettingsDocument
{
    public const string SettingsKey = "folder-view-settings";
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("rules")]
    public List<FolderViewRule> Rules { get; set; } = [];

    public static FolderViewSettingsDocument FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new FolderViewSettingsDocument();
        }

        try
        {
            var document = JsonSerializer.Deserialize<FolderViewSettingsDocument>(json)
                ?? new FolderViewSettingsDocument();
            document.Normalize();
            return document;
        }
        catch
        {
            return new FolderViewSettingsDocument();
        }
    }

    public string ToJson()
    {
        Normalize();
        return JsonSerializer.Serialize(this, FolderViewJson.Options);
    }

    public FolderViewRule Upsert(
        FolderViewScope scope,
        string path,
        IReadOnlyList<DriveInfo> drives,
        FolderViewOptions options)
    {
        var normalizedScope = FolderViewRule.ScopeToText(scope);
        var location = scope == FolderViewScope.Global
            ? FolderViewLocationKey.Global
            : FolderViewLocationKey.FromPath(path, drives);
        if (scope != FolderViewScope.Global && string.IsNullOrWhiteSpace(location.Path))
        {
            throw new ArgumentException("Folder view settings require a folder path.", nameof(path));
        }

        var now = DateTimeOffset.UtcNow;
        var existing = Rules.FirstOrDefault(rule => rule.MatchesIdentity(normalizedScope, location));
        if (existing is null)
        {
            existing = new FolderViewRule
            {
                Id = NewId(),
                CreatedAt = now,
            };
            Rules.Add(existing);
        }

        existing.Scope = normalizedScope;
        existing.Path = scope == FolderViewScope.Global ? "" : location.Path;
        existing.PathKey = scope == FolderViewScope.Global ? "" : location.PathKey;
        existing.StableKey = scope == FolderViewScope.Global ? "" : location.StableKey;
        existing.Options = options.Clone();
        existing.UpdatedAt = now;
        Normalize();

        return Rules.FirstOrDefault(rule => rule.MatchesIdentity(normalizedScope, location))?.Clone()
            ?? existing.Clone();
    }

    public FolderViewRule? Resolve(string path, IReadOnlyList<DriveInfo> drives)
    {
        Normalize();
        var location = FolderViewLocationKey.FromPath(path, drives);
        var global = Rules.LastOrDefault(rule => rule.ScopeValue == FolderViewScope.Global);
        FolderViewRule? exact = null;
        FolderViewRule? descendant = null;
        var descendantScore = -1;

        foreach (var rule in Rules)
        {
            if (rule.ScopeValue == FolderViewScope.Folder && rule.ContainsExact(location))
            {
                exact = rule;
            }
            else if (rule.ScopeValue == FolderViewScope.Descendants
                && rule.ContainsDescendant(location, out var score)
                && score >= descendantScore)
            {
                descendant = rule;
                descendantScore = score;
            }
        }

        return (exact ?? descendant ?? global)?.Clone();
    }

    public void Normalize()
    {
        Version = CurrentVersion;
        var cleaned = new List<FolderViewRule>();
        var seen = new Dictionary<string, FolderViewRule>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in Rules)
        {
            if (rule is null)
            {
                continue;
            }

            rule.Normalize();
            if (rule.ScopeValue != FolderViewScope.Global && string.IsNullOrWhiteSpace(rule.Path))
            {
                continue;
            }

            var key = rule.IdentityKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            seen[key] = rule;
        }

        foreach (var rule in seen.Values)
        {
            cleaned.Add(rule);
        }

        Rules = cleaned;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}

public sealed class FolderViewRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = FolderViewRuleScope.Global;

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("pathKey")]
    public string PathKey { get; set; } = "";

    [JsonPropertyName("stableKey")]
    public string StableKey { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("options")]
    public FolderViewOptions Options { get; set; } = new();

    [JsonIgnore]
    public FolderViewScope ScopeValue => TextToScope(Scope);

    [JsonIgnore]
    public string ScopeLabel => ScopeValue switch
    {
        FolderViewScope.Folder => "this folder",
        FolderViewScope.Descendants => "this folder and descendants",
        _ => "global",
    };

    [JsonIgnore]
    public string IdentityKey
    {
        get
        {
            var scope = ScopeToText(ScopeValue);
            if (ScopeValue == FolderViewScope.Global)
            {
                return scope;
            }

            var location = FolderViewLocationKey.FromStored(Path, PathKey, StableKey);
            return $"{scope}|{location.BestIdentity}";
        }
    }

    public FolderViewRule Clone()
    {
        return JsonSerializer.Deserialize<FolderViewRule>(JsonSerializer.Serialize(this, FolderViewJson.Options), FolderViewJson.Options)
            ?? new FolderViewRule();
    }

    internal bool MatchesIdentity(string scope, FolderViewLocationKey location)
    {
        if (!string.Equals(ScopeToText(ScopeValue), scope, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TextToScope(scope) == FolderViewScope.Global)
        {
            return true;
        }

        var current = FolderViewLocationKey.FromStored(Path, PathKey, StableKey);
        return current.SameIdentity(location);
    }

    internal bool ContainsExact(FolderViewLocationKey location)
    {
        var current = FolderViewLocationKey.FromStored(Path, PathKey, StableKey);
        return current.SameIdentity(location)
            || PathRules.PathsEqual(current.Path, location.Path);
    }

    internal bool ContainsDescendant(FolderViewLocationKey location, out int score)
    {
        score = -1;
        var current = FolderViewLocationKey.FromStored(Path, PathKey, StableKey);
        if (!current.Contains(location))
        {
            return false;
        }

        score = Math.Max(current.PathKey.Length, current.StableKey.Length);
        return true;
    }

    public void Normalize()
    {
        Scope = ScopeToText(TextToScope(Scope));
        Path = ScopeValue == FolderViewScope.Global ? "" : (Path ?? "").Trim();
        PathKey = ScopeValue == FolderViewScope.Global ? "" : FolderViewLocationKey.NormalizePathKey(PathKey, Path);
        StableKey = ScopeValue == FolderViewScope.Global ? "" : (StableKey ?? "").Trim();
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        CreatedAt = CreatedAt == default ? DateTimeOffset.UtcNow : CreatedAt;
        UpdatedAt = UpdatedAt == default ? CreatedAt : UpdatedAt;
        Options ??= new FolderViewOptions();
        Options.Normalize();
    }

    public static string ScopeToText(FolderViewScope scope) => scope switch
    {
        FolderViewScope.Folder => FolderViewRuleScope.Folder,
        FolderViewScope.Descendants => FolderViewRuleScope.Descendants,
        _ => FolderViewRuleScope.Global,
    };

    public static FolderViewScope TextToScope(string? scope)
    {
        return (scope ?? "").Trim().ToLowerInvariant() switch
        {
            FolderViewRuleScope.Folder => FolderViewScope.Folder,
            FolderViewRuleScope.Descendants => FolderViewScope.Descendants,
            "folder-and-descendants" => FolderViewScope.Descendants,
            "children" => FolderViewScope.Descendants,
            _ => FolderViewScope.Global,
        };
    }
}

public sealed class FolderViewOptions
{
    [JsonPropertyName("view")]
    public string? View { get; set; }

    [JsonPropertyName("iconSize")]
    public int? IconSize { get; set; }

    [JsonPropertyName("visibleColumnIds")]
    public List<string>? VisibleColumnIds { get; set; }

    [JsonPropertyName("columnWidths")]
    public Dictionary<string, double>? ColumnWidths { get; set; }

    [JsonPropertyName("sortBy")]
    public string? SortBy { get; set; }

    [JsonPropertyName("sortAscending")]
    public bool? SortAscending { get; set; }

    [JsonPropertyName("groupBy")]
    public string? GroupBy { get; set; }

    [JsonPropertyName("groupAscending")]
    public bool? GroupAscending { get; set; }

    [JsonPropertyName("previewVisible")]
    public bool? PreviewVisible { get; set; }

    [JsonPropertyName("showHidden")]
    public bool? ShowHidden { get; set; }

    [JsonPropertyName("workspaceProfileId")]
    public string? WorkspaceProfileId { get; set; }

    [JsonPropertyName("columnPreset")]
    public string? ColumnPreset { get; set; }

    public FolderViewOptions Clone()
    {
        return JsonSerializer.Deserialize<FolderViewOptions>(JsonSerializer.Serialize(this, FolderViewJson.Options), FolderViewJson.Options)
            ?? new FolderViewOptions();
    }

    public void Normalize()
    {
        View = NormalizeOptionalView(View);
        IconSize = IconSize is null ? null : UiSettings.NormalizeIconSize(IconSize);
        ColumnPreset = string.IsNullOrWhiteSpace(ColumnPreset)
            ? null
            : UiSettings.NormalizeColumnPreset(ColumnPreset);
        VisibleColumnIds = NormalizeVisibleColumns(VisibleColumnIds);
        ColumnWidths = NormalizeColumnWidths(ColumnWidths);
        SortBy = NormalizeOptionalToken(SortBy);
        GroupBy = NormalizeOptionalToken(GroupBy);
        WorkspaceProfileId = NormalizeOptionalToken(WorkspaceProfileId);
    }

    private static string? NormalizeOptionalView(string? view)
    {
        return string.IsNullOrWhiteSpace(view)
            ? null
            : UiSettings.NormalizeDefaultView(view);
    }

    private static string? NormalizeOptionalToken(string? value)
    {
        var trimmed = (value ?? "").Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static List<string>? NormalizeVisibleColumns(IEnumerable<string>? ids)
    {
        if (ids is null)
        {
            return null;
        }

        var columns = new ColumnLayout();
        var cleaned = ids
            .Where(id => !string.IsNullOrWhiteSpace(id) && columns.Find(id.Trim()) is not null)
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return cleaned.Count == 0 ? null : cleaned;
    }

    private static Dictionary<string, double>? NormalizeColumnWidths(IDictionary<string, double>? widths)
    {
        if (widths is null)
        {
            return null;
        }

        var columns = new ColumnLayout();
        var cleaned = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (id, width) in widths)
        {
            var column = columns.Find(id);
            if (column is null || double.IsNaN(width) || double.IsInfinity(width))
            {
                continue;
            }

            cleaned[column.Id] = Math.Clamp(width, column.MinWidth, column.MaxWidth);
        }

        return cleaned.Count == 0 ? null : cleaned;
    }
}

public static class FolderViewRuleScope
{
    public const string Global = "global";
    public const string Folder = "folder";
    public const string Descendants = "descendants";
}

internal sealed class FolderViewLocationKey
{
    private FolderViewLocationKey(string path, string pathKey, string stableKey)
    {
        Path = path;
        PathKey = pathKey;
        StableKey = stableKey;
    }

    public static FolderViewLocationKey Global { get; } = new("", "", "");

    public string Path { get; }
    public string PathKey { get; }
    public string StableKey { get; }
    public string BestIdentity => !string.IsNullOrWhiteSpace(StableKey) ? StableKey : PathKey;

    public static FolderViewLocationKey FromPath(string? path, IReadOnlyList<DriveInfo> drives)
    {
        var cleaned = (path ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return new FolderViewLocationKey("", "", "");
        }

        var pathKey = BuildPathKey(cleaned);
        var stableKey = StableKeyFromPath(cleaned, drives);
        return new FolderViewLocationKey(cleaned, pathKey, stableKey);
    }

    public static FolderViewLocationKey FromStored(string? path, string? pathKey, string? stableKey)
    {
        var cleanedPath = (path ?? "").Trim();
        return new FolderViewLocationKey(
            cleanedPath,
            NormalizePathKey(pathKey, cleanedPath),
            (stableKey ?? "").Trim());
    }

    public static string NormalizePathKey(string? pathKey, string? fallbackPath)
    {
        var normalized = (pathKey ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        return string.IsNullOrWhiteSpace(fallbackPath) ? "" : BuildPathKey(fallbackPath);
    }

    public bool SameIdentity(FolderViewLocationKey other)
    {
        if (!string.IsNullOrWhiteSpace(StableKey) && !string.IsNullOrWhiteSpace(other.StableKey))
        {
            return string.Equals(StableKey, other.StableKey, StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(PathKey)
            && string.Equals(PathKey, other.PathKey, StringComparison.OrdinalIgnoreCase);
    }

    public bool Contains(FolderViewLocationKey child)
    {
        if (!string.IsNullOrWhiteSpace(StableKey) && !string.IsNullOrWhiteSpace(child.StableKey))
        {
            return StableContains(StableKey, child.StableKey);
        }

        if (!string.IsNullOrWhiteSpace(Path) && !string.IsNullOrWhiteSpace(child.Path))
        {
            return PathRules.PathContains(Path, child.Path);
        }

        return !string.IsNullOrWhiteSpace(PathKey)
            && PathKeyContains(PathKey, child.PathKey);
    }

    private static string BuildPathKey(string path)
    {
        return "path:" + PathRules.NormalizeComparablePath(path.Trim());
    }

    private static bool PathKeyContains(string parentKey, string childKey)
    {
        if (string.Equals(parentKey, childKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = parentKey.EndsWith("/", StringComparison.Ordinal) ? parentKey : parentKey + "/";
        return childKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StableContains(string parentKey, string childKey)
    {
        if (string.IsNullOrWhiteSpace(parentKey) || string.IsNullOrWhiteSpace(childKey))
        {
            return false;
        }

        if (string.Equals(parentKey, childKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = parentKey.EndsWith("/", StringComparison.Ordinal) ? parentKey : parentKey + "/";
        return childKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string StableKeyFromPath(string path, IReadOnlyList<DriveInfo> drives)
    {
        if (TryBuildUncStableKey(path, out var directUnc))
        {
            return directUnc;
        }

        var drive = DrivePresentation.FindDriveForPath(path, drives);
        if (drive is null)
        {
            return "";
        }

        var relative = RelativeToRoot(path, drive.Path);
        if (!string.IsNullOrWhiteSpace(drive.RemotePath))
        {
            return TryBuildUncStableKey(PathRules.JoinPath(drive.RemotePath, relative), out var mappedUnc)
                ? mappedUnc
                : $"remote:{NormalizeStablePath(drive.RemotePath)}/{NormalizeStablePath(relative)}".TrimEnd('/');
        }

        if (IsRemovableLike(drive))
        {
            var identity = string.Join(
                ":",
                SanitizeStableToken(drive.DriveType),
                SanitizeStableToken(drive.Name),
                SanitizeStableToken(drive.FileSystem),
                drive.TotalSpace.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return $"volume:{identity}/{NormalizeStablePath(relative)}".TrimEnd('/');
        }

        return "";
    }

    private static bool TryBuildUncStableKey(string path, out string key)
    {
        key = "";
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        if (!normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = normalized
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToArray();
        if (parts.Length < 2)
        {
            return false;
        }

        var segments = parts.Select(NormalizeStablePathSegment);
        key = "unc://" + string.Join("/", segments);
        return true;
    }

    private static string RelativeToRoot(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return "";
        }

        var normalizedPath = PathRules.NormalizeComparablePath(path);
        var normalizedRoot = PathRules.NormalizeComparablePath(root);
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.Ordinal))
        {
            return "";
        }

        var prefix = normalizedRoot.EndsWith('/') ? normalizedRoot : normalizedRoot + "/";
        return normalizedPath.StartsWith(prefix, StringComparison.Ordinal)
            ? normalizedPath[prefix.Length..]
            : "";
    }

    private static bool IsRemovableLike(DriveInfo drive)
    {
        return (drive.DriveType ?? "").Trim().ToLowerInvariant() is "removable" or "cd-rom" or "optical";
    }

    private static string NormalizeStablePath(string? path)
    {
        return string.Join(
            "/",
            (path ?? "")
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeStablePathSegment));
    }

    private static string NormalizeStablePathSegment(string segment)
    {
        return Uri.EscapeDataString(segment.Trim().ToLowerInvariant());
    }

    private static string SanitizeStableToken(string? value)
    {
        var trimmed = (value ?? "").Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(trimmed) ? "unknown" : Uri.EscapeDataString(trimmed);
    }
}

internal static class FolderViewJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
