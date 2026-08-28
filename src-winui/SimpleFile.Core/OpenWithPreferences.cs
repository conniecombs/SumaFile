using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleFile.Core;

public sealed class OpenWithPreferences
{
    public const string SettingsKey = "openWith.preferences.v1";
    public const int MaxMenuApplications = 8;
    public const int MaxRecentApplications = 8;
    public const int MaxFavoriteApplications = 12;

    [JsonPropertyName("favoritesByExtension")]
    public Dictionary<string, List<OpenWithSavedApplication>> FavoritesByExtension { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("recentsByExtension")]
    public Dictionary<string, List<OpenWithSavedApplication>> RecentsByExtension { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static OpenWithPreferences FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new OpenWithPreferences();
        }

        try
        {
            var preferences = JsonSerializer.Deserialize<OpenWithPreferences>(json);
            return preferences?.Normalized() ?? new OpenWithPreferences();
        }
        catch (JsonException)
        {
            return new OpenWithPreferences();
        }
    }

    public string ToJson()
    {
        NormalizeInPlace();
        return JsonSerializer.Serialize(this);
    }

    public IReadOnlyList<OpenWithApplication> ComposeMenuApplications(
        string? extension,
        IEnumerable<OpenWithApplication> discovered)
    {
        var normalizedExtension = NormalizeExtension(extension);
        var result = new List<OpenWithApplication>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddSaved(result, seen, FavoritesByExtension, normalizedExtension, "favorite", isFavorite: true, isRecent: false);
        AddSaved(result, seen, RecentsByExtension, normalizedExtension, "recent", isFavorite: false, isRecent: true);
        foreach (var app in discovered)
        {
            AddApp(result, seen, app);
        }

        return result.Take(MaxMenuApplications).ToArray();
    }

    public void RecordRecent(string? extension, OpenWithApplication application)
    {
        var normalizedExtension = NormalizeExtension(extension);
        AddSavedApplication(
            RecentsByExtension,
            normalizedExtension,
            application,
            MaxRecentApplications,
            preserveExistingOrder: false);
    }

    public void PinForExtension(string? extension, OpenWithApplication application)
    {
        var normalizedExtension = NormalizeExtension(extension);
        AddSavedApplication(
            FavoritesByExtension,
            normalizedExtension,
            application,
            MaxFavoriteApplications,
            preserveExistingOrder: true);
    }

    public void UnpinForExtension(string? extension, OpenWithApplication application)
    {
        var normalizedExtension = NormalizeExtension(extension);
        RemoveSavedApplication(FavoritesByExtension, normalizedExtension, application);
    }

    public static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return "*";
        }

        var trimmed = extension.Trim();
        if (trimmed == "*")
        {
            return "*";
        }

        return trimmed.StartsWith(".", StringComparison.Ordinal)
            ? trimmed.ToLowerInvariant()
            : "." + trimmed.ToLowerInvariant();
    }

    private OpenWithPreferences Normalized()
    {
        NormalizeInPlace();
        return this;
    }

    private void NormalizeInPlace()
    {
        FavoritesByExtension = NormalizeMap(FavoritesByExtension, MaxFavoriteApplications);
        RecentsByExtension = NormalizeMap(RecentsByExtension, MaxRecentApplications);
    }

    private static Dictionary<string, List<OpenWithSavedApplication>> NormalizeMap(
        Dictionary<string, List<OpenWithSavedApplication>>? source,
        int maxCount)
    {
        var result = new Dictionary<string, List<OpenWithSavedApplication>>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return result;
        }

        foreach (var pair in source)
        {
            var extension = NormalizeExtension(pair.Key);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var apps = pair.Value
                .Where(app => !string.IsNullOrWhiteSpace(app.ApplicationPath))
                .Where(app => seen.Add(app.ApplicationPath.Trim()))
                .Select(app => app.Normalized())
                .Take(maxCount)
                .ToList();

            if (apps.Count > 0)
            {
                result[extension] = apps;
            }
        }

        return result;
    }

    private static void AddSaved(
        List<OpenWithApplication> result,
        HashSet<string> seen,
        Dictionary<string, List<OpenWithSavedApplication>> source,
        string extension,
        string appSource,
        bool isFavorite,
        bool isRecent)
    {
        if (!source.TryGetValue(extension, out var apps))
        {
            return;
        }

        foreach (var app in apps)
        {
            AddApp(result, seen, app.ToApplication(appSource, isFavorite, isRecent));
        }
    }

    private static void AddApp(
        List<OpenWithApplication> result,
        HashSet<string> seen,
        OpenWithApplication app)
    {
        if (string.IsNullOrWhiteSpace(app.ApplicationPath) || !seen.Add(app.ApplicationPath.Trim()))
        {
            return;
        }

        result.Add(app with
        {
            ApplicationPath = app.ApplicationPath.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(app.DisplayName)
                ? Path.GetFileNameWithoutExtension(app.ApplicationPath)
                : app.DisplayName.Trim(),
        });
    }

    private static void AddSavedApplication(
        Dictionary<string, List<OpenWithSavedApplication>> target,
        string extension,
        OpenWithApplication application,
        int maxCount,
        bool preserveExistingOrder)
    {
        if (string.IsNullOrWhiteSpace(application.ApplicationPath))
        {
            return;
        }

        if (!target.TryGetValue(extension, out var apps))
        {
            apps = [];
            target[extension] = apps;
        }

        var saved = OpenWithSavedApplication.From(application);
        apps.RemoveAll(app => string.Equals(
            app.ApplicationPath.Trim(),
            saved.ApplicationPath,
            StringComparison.OrdinalIgnoreCase));

        if (preserveExistingOrder)
        {
            apps.Add(saved);
        }
        else
        {
            apps.Insert(0, saved);
        }

        if (apps.Count > maxCount)
        {
            apps.RemoveRange(maxCount, apps.Count - maxCount);
        }
    }

    private static void RemoveSavedApplication(
        Dictionary<string, List<OpenWithSavedApplication>> target,
        string extension,
        OpenWithApplication application)
    {
        if (string.IsNullOrWhiteSpace(application.ApplicationPath)
            || !target.TryGetValue(extension, out var apps))
        {
            return;
        }

        apps.RemoveAll(app => string.Equals(
            app.ApplicationPath.Trim(),
            application.ApplicationPath.Trim(),
            StringComparison.OrdinalIgnoreCase));

        if (apps.Count == 0)
        {
            target.Remove(extension);
        }
    }
}

public sealed class OpenWithSavedApplication
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("applicationPath")]
    public string ApplicationPath { get; set; } = "";

    public static OpenWithSavedApplication From(OpenWithApplication application)
    {
        return new OpenWithSavedApplication
        {
            DisplayName = string.IsNullOrWhiteSpace(application.DisplayName)
                ? Path.GetFileNameWithoutExtension(application.ApplicationPath)
                : application.DisplayName.Trim(),
            ApplicationPath = application.ApplicationPath.Trim(),
        };
    }

    public OpenWithApplication ToApplication(string source, bool isFavorite, bool isRecent)
    {
        return new OpenWithApplication
        {
            DisplayName = DisplayName,
            ApplicationPath = ApplicationPath,
            Source = source,
            IsFavorite = isFavorite,
            IsRecent = isRecent,
        };
    }

    public OpenWithSavedApplication Normalized()
    {
        return new OpenWithSavedApplication
        {
            DisplayName = string.IsNullOrWhiteSpace(DisplayName)
                ? Path.GetFileNameWithoutExtension(ApplicationPath)
                : DisplayName.Trim(),
            ApplicationPath = ApplicationPath.Trim(),
        };
    }
}
