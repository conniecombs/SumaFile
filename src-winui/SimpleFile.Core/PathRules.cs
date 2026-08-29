using SimpleFile.Ipc;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Core;

/// <summary>
/// Path helpers ported from frontend/src/lib/coreFileManager.ts.
/// </summary>
public static class PathRules
{
    public const string RecycleBinPath = "recycle-bin:";

    public static bool IsRecycleBinPath(string? path)
    {
        return string.Equals((path ?? "").Trim(), RecycleBinPath, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWindowsRoot(string path)
    {
        var trimmed = path.Trim();
        return trimmed.Length is 2 or 3
            && char.IsAsciiLetter(trimmed[0])
            && trimmed[1] == ':'
            && (trimmed.Length == 2 || trimmed[2] is '\\' or '/');
    }

    public static bool IsRootPath(string path)
    {
        var trimmed = path.Trim();
        return trimmed == "/" || IsWindowsRoot(trimmed) || IsRecycleBinPath(trimmed);
    }

    public static char PathSeparator(string path)
    {
        return path.Contains('/') && !path.Contains('\\') ? '/' : '\\';
    }

    public static string TrimTrailingSeparators(string path)
    {
        var trimmed = path.Trim();
        if (IsRootPath(trimmed))
        {
            return IsWindowsRoot(trimmed) ? $"{trimmed[..2]}\\" : "/";
        }

        return trimmed.TrimEnd('\\', '/');
    }

    public static string JoinPath(string parent, string name)
    {
        var cleanParent = TrimTrailingSeparators(parent);
        if (cleanParent == "/")
        {
            return $"/{name}";
        }

        return $"{cleanParent}{PathSeparator(cleanParent)}{name}";
    }

    public static string? GetParentPath(string path)
    {
        var cleanPath = TrimTrailingSeparators(path);
        if (string.IsNullOrEmpty(cleanPath) || IsRootPath(cleanPath))
        {
            return null;
        }

        var lastSeparator = Math.Max(cleanPath.LastIndexOf('\\'), cleanPath.LastIndexOf('/'));
        if (lastSeparator < 0)
        {
            return null;
        }

        if (lastSeparator == 2 && cleanPath[1] == ':')
        {
            return $"{cleanPath[..2]}\\";
        }

        if (lastSeparator == 0)
        {
            return "/";
        }

        return cleanPath[..lastSeparator];
    }

    public static string Basename(string path)
    {
        var cleanPath = TrimTrailingSeparators(path);
        if (IsWindowsRoot(cleanPath))
        {
            return cleanPath[..2];
        }

        if (cleanPath == "/")
        {
            return "/";
        }

        var parts = cleanPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? cleanPath : parts[^1];
    }

    public static string NormalizeComparablePath(string path)
    {
        return path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
    }

    public static bool PathsEqual(string left, string right)
    {
        return NormalizeComparablePath(left) == NormalizeComparablePath(right);
    }

    public static bool PathContains(string parent, string child)
    {
        var parentPath = NormalizeComparablePath(parent);
        var childPath = NormalizeComparablePath(child);
        if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(childPath))
        {
            return false;
        }

        if (parentPath == childPath)
        {
            return true;
        }

        var prefix = parentPath.EndsWith('/') ? parentPath : parentPath + "/";
        return childPath.StartsWith(prefix, StringComparison.Ordinal);
    }

    public static DriveInfo? CreateFallbackDriveForPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(path, @"^[a-zA-Z]:[\\/]");
        var rootPath = match.Success ? match.Value : "/";
        var isWindows = match.Success;
        return new DriveInfo
        {
            DriveType = isWindows ? "Fixed" : "Mount",
            DriveStatus = "available",
            FreeSpace = 0,
            Name = isWindows ? $"Local Disk ({rootPath[..2]})" : rootPath,
            Path = rootPath,
            RemotePath = null,
            StatusDetail = null,
            TotalSpace = 0,
        };
    }

    public static bool IsNetworkFsPath(string path, IReadOnlyList<DriveInfo> drives)
    {
        var trimmed = (path ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        if (trimmed.Length < 2 || trimmed[1] != ':' || !char.IsAsciiLetter(trimmed[0]))
        {
            return false;
        }

        var root = $"{char.ToUpperInvariant(trimmed[0])}:\\";
        var drive = drives.FirstOrDefault(candidate =>
        {
            var candidateRoot = (candidate.Path ?? "").Replace('/', '\\').ToUpperInvariant();
            return candidateRoot == root || candidateRoot == root[..2];
        });

        return string.Equals(drive?.DriveType, "network", StringComparison.OrdinalIgnoreCase);
    }
}
