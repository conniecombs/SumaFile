using System.IO;

namespace SimpleFile.Core;

public sealed record OpenWithApplication
{
    public string DisplayName { get; init; } = "";
    public string ApplicationPath { get; init; } = "";
    public string Source { get; init; } = "discovered";
    public bool IsFavorite { get; init; }
    public bool IsRecent { get; init; }

    public string MenuLabel => DisplayName.Trim().Length > 0 ? DisplayName.Trim() : ApplicationPath;

    public static OpenWithApplication FromPath(string applicationPath, string? displayName = null, string source = "custom")
    {
        return new OpenWithApplication
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? Path.GetFileNameWithoutExtension(applicationPath)
                : displayName.Trim(),
            ApplicationPath = applicationPath.Trim(),
            Source = source,
        };
    }
}
