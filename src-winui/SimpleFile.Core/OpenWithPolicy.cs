using System.IO;

namespace SimpleFile.Core;

/// <summary>
/// Shared Open With rules generated from ipc/schema/v1/open-with-policy.json.
/// </summary>
public static class OpenWithPolicy
{
    private static readonly HashSet<string> DeniedTargetExtensions = new(
        OpenWithPolicyGenerated.DeniedTargetExtensions,
        StringComparer.OrdinalIgnoreCase);

    public static bool IsDeniedTargetExtension(string? extension)
    {
        var normalized = OpenWithPreferences.NormalizeExtension(extension);
        return normalized != "*" && DeniedTargetExtensions.Contains(normalized);
    }

    public static bool IsDeniedTargetPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return IsDeniedTargetExtension(Path.GetExtension(path));
    }

    public static bool IsUnc(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    public static bool ContainsParentDirectory(string path)
    {
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => part == "..");
    }

    public static bool IsTrustedApplicationRoot(string applicationPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(applicationPath) || IsUnc(applicationPath) || ContainsParentDirectory(applicationPath))
            {
                return false;
            }

            var candidate = Path.GetFullPath(Environment.ExpandEnvironmentVariables(applicationPath.Trim()));
            return TrustedApplicationRoots().Any(root =>
                !string.IsNullOrWhiteSpace(root)
                && IsUnderRoot(candidate, Path.GetFullPath(root)));
        }
        catch
        {
            return false;
        }
    }

    public static bool IsLaunchableApplication(string applicationPath)
    {
        if (string.IsNullOrWhiteSpace(applicationPath))
        {
            return false;
        }

        var expanded = Environment.ExpandEnvironmentVariables(applicationPath.Trim());
        if (IsUnc(expanded) || ContainsParentDirectory(expanded))
        {
            return false;
        }

        var extension = Path.GetExtension(expanded);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsTrustedApplicationRoot(expanded))
        {
            return false;
        }

        try
        {
            return File.Exists(expanded);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnderRoot(string candidate, string root)
    {
        var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> TrustedApplicationRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "WindowsApps");
        }
    }
}
