using System.Runtime.InteropServices;

namespace SimpleFile.Core;

/// <summary>Whether a path is there, as far as the file system can say.</summary>
public enum PathExistence
{
    /// <summary>The file system answers that nothing has that name.</summary>
    Missing,
    Exists,
    /// <summary>
    /// The check itself failed: access denied, a share that is offline, a path the call could not take. The
    /// path may well be there.
    /// </summary>
    Unknown,
}

/// <summary>
/// Win32 path helpers that match backend no-follow semantics (<c>symlink_metadata</c> /
/// <c>path_exists_no_follow</c>): report whether the final path component exists without
/// resolving junctions or symbolic links.
/// </summary>
public static class NativePath
{
    private const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;

    /// <summary>
    /// From this length a path is given to Win32 with the <c>\\?\</c> prefix, as Rust's std gives the service's
    /// paths: the app does not declare long-path awareness, so without it a call fails past 260 characters.
    /// </summary>
    internal const int LongPathLength = 248;

    /// <summary>True only when the path is known to be there; false also when it could not be checked.</summary>
    public static bool ExistsNoFollow(string? path) => Existence(path) == PathExistence.Exists;

    /// <summary>
    /// Whether the final component of <paramref name="path"/> is there, without following a link. Missing only
    /// when the file system says so; any other failure of the check is <see cref="PathExistence.Unknown"/>.
    /// </summary>
    public static PathExistence Existence(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return PathExistence.Unknown;
        }

        if (!OperatingSystem.IsWindows())
        {
            return ExistenceElsewhere(path);
        }

        if (Win32Path(path) is not { } native)
        {
            return PathExistence.Unknown;
        }

        var attributes = GetFileAttributesW(native);
        return attributes != INVALID_FILE_ATTRIBUTES
            ? PathExistence.Exists
            : FromWin32Error(Marshal.GetLastWin32Error());
    }

    public static bool IsDirectoryNoFollow(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            var directory = new DirectoryInfo(path);
            return directory.Exists && directory.LinkTarget is null;
        }

        var attributes = Win32Path(path) is { } native ? GetFileAttributesW(native) : INVALID_FILE_ATTRIBUTES;
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & (uint)FileAttributes.Directory) == 0)
            return false;
        // Cloud placeholder directories can be reparse points without redirecting the path.
        return (attributes & (uint)FileAttributes.ReparsePoint) == 0 || new DirectoryInfo(path).LinkTarget is null;
    }

    /// <summary>What a failed <c>GetFileAttributesW</c> says about the path.</summary>
    internal static PathExistence FromWin32Error(int error) =>
        error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND ? PathExistence.Missing : PathExistence.Unknown;

    /// <summary>
    /// The path a Win32 call is given: unchanged when short, otherwise made absolute and given the <c>\\?\</c>
    /// prefix. Null when it cannot be made absolute.
    /// </summary>
    internal static string? Win32Path(string path)
    {
        if (path.Length < LongPathLength || IsVerbatim(path))
        {
            return path;
        }

        try
        {
            return VerbatimForm(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// An absolute path with the prefix that lifts Win32's length limit: <c>C:\…</c> becomes <c>\\?\C:\…</c>,
    /// <c>\\server\share\…</c> becomes <c>\\?\UNC\server\share\…</c>, <c>\\.\…</c> becomes <c>\\?\…</c>.
    /// Anything else is returned as it is.
    /// </summary>
    internal static string VerbatimForm(string fullPath)
    {
        if (IsVerbatim(fullPath))
        {
            return fullPath;
        }

        if (fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return @"\\?\" + fullPath[4..];
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + fullPath[2..];
        }

        return fullPath.Length >= 3 && char.IsAsciiLetter(fullPath[0]) && fullPath[1] == ':' && fullPath[2] == '\\'
            ? @"\\?\" + fullPath
            : fullPath;
    }

    private static bool IsVerbatim(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal);

    /// <summary>The app runs on Windows only; this keeps the shared logic testable elsewhere.</summary>
    private static PathExistence ExistenceElsewhere(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists || Directory.Exists(path) || file.LinkTarget is not null)
            {
                return PathExistence.Exists;
            }

            // The checks above say false for an error too; this one tells a missing path from a failed check.
            _ = File.GetAttributes(path);
            return PathExistence.Exists;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return PathExistence.Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return PathExistence.Unknown;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string lpFileName);
}
