namespace SimpleFile.Core;

/// <summary>
/// Builds version-stamped cache keys and byte estimates for session-only UI thumbnail and icon caches.
/// </summary>
public static class ThumbnailCacheKey
{
    private const char Separator = '\u001f';

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var trimmed = path.Trim().Replace('/', '\\');
        while (trimmed.Length > 3 && trimmed.EndsWith('\\'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed;
    }

    public static string BuildVersionToken(ulong size, string? modified, string? fallbackPath = null)
    {
        var normalizedModified = modified?.Trim() ?? "";
        if (size > 0 || normalizedModified.Length > 0)
        {
            return $"{size}:{normalizedModified}";
        }

        if (!string.IsNullOrWhiteSpace(fallbackPath))
        {
            var probed = ProbeFileVersionToken(fallbackPath);
            if (probed.Length > 0)
            {
                return probed;
            }
        }

        return "0:";
    }

    public static string ProbeFileVersionToken(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "0:";
        }

        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return "0:";
        }

        try
        {
            var info = new FileInfo(trimmed);
            if (info.Exists)
            {
                return $"{(ulong)Math.Max(0, info.Length)}:{info.LastWriteTimeUtc.Ticks}";
            }
        }
        catch
        {
            // Best-effort filesystem probe.
        }

        return "0:";
    }

    public static string Create(
        string path,
        ulong size,
        string? modified,
        int displayPixelSize,
        string? videoFrameToken = null)
    {
        var normalizedPath = NormalizePath(path);
        var versionToken = BuildVersionToken(size, modified, normalizedPath);
        var normalizedSize = Math.Max(1, displayPixelSize);
        var framePart = videoFrameToken?.Trim() ?? "";
        return $"{normalizedPath}{Separator}{versionToken}{Separator}{normalizedSize}{Separator}{framePart}";
    }

    public static string CreateIconKey(
        string path,
        bool isDirectory,
        bool fileSpecific,
        int displayPixelSize)
    {
        var normalizedSize = Math.Max(1, displayPixelSize);
        if (isDirectory)
        {
            return $"dir{Separator}0:{Separator}{normalizedSize}{Separator}icon";
        }

        if (fileSpecific)
        {
            var normalizedPath = NormalizePath(path);
            var version = ProbeFileVersionToken(normalizedPath);
            return $"{normalizedPath}{Separator}{version}{Separator}{normalizedSize}{Separator}icon";
        }

        var extension = Path.GetExtension(path);
        var normalizedExt = string.IsNullOrWhiteSpace(extension)
            ? "file"
            : extension.Trim().ToLowerInvariant();
        return $"ext:{normalizedExt}{Separator}0:{Separator}{normalizedSize}{Separator}icon";
    }

    public static string CreatePathIconKey(string path, bool treatAsDirectory, int displayPixelSize)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedSize = Math.Max(1, displayPixelSize);
        var kind = treatAsDirectory ? "dir" : "file";
        var version = !treatAsDirectory && ShouldProbePathIconVersion(normalizedPath)
            ? ProbeFileVersionToken(normalizedPath)
            : "0:";
        return $"{normalizedPath}{Separator}{version}{Separator}{normalizedSize}{Separator}path-icon:{kind}";
    }

    private static bool ShouldProbePathIconVersion(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".exe" or ".ico" or ".cur" or ".lnk" or ".url";
    }

    public static string ExtractPath(string? cacheKey)
    {
        if (string.IsNullOrEmpty(cacheKey))
        {
            return "";
        }

        var index = cacheKey.IndexOf(Separator);
        return index >= 0 ? cacheKey[..index] : cacheKey;
    }

    public static bool MatchesPathOrSubtree(string? cacheKey, string? targetPath)
    {
        var keyPath = ExtractPath(cacheKey);
        var normalizedTarget = NormalizePath(targetPath);
        if (keyPath.Length == 0 || normalizedTarget.Length == 0)
        {
            return false;
        }

        if (PathRules.PathsEqual(keyPath, normalizedTarget))
        {
            return true;
        }

        if (!keyPath.StartsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (normalizedTarget.EndsWith('\\'))
        {
            return true;
        }

        return keyPath.Length > normalizedTarget.Length
            && keyPath[normalizedTarget.Length] == '\\';
    }

    public static long EstimateDecodedBytes(int displayPixelSize, string? cacheKey = null, int encodedBytes = 0)
    {
        var clamped = Math.Max(1, displayPixelSize);
        var decodedRgbaBytes = (long)clamped * clamped * 4L;
        var payloadBytes = Math.Max(decodedRgbaBytes, Math.Max(0, encodedBytes));
        var keyBytes = (cacheKey?.Length ?? 0) * 2L;
        return payloadBytes + keyBytes;
    }
}
