using System.Runtime.InteropServices;

namespace SimpleFile.Core;

/// <summary>
/// Win32 path helpers that match backend no-follow semantics (<c>symlink_metadata</c> /
/// <c>path_exists_no_follow</c>): report whether the final path component exists without
/// resolving junctions or symbolic links.
/// </summary>
public static class NativePath
{
    private const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;

    public static bool ExistsNoFollow(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var attributes = GetFileAttributesW(path);
        return attributes != INVALID_FILE_ATTRIBUTES;
    }

    public static bool IsDirectoryNoFollow(string path)
    {
        var attributes = GetFileAttributesW(path);
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & (uint)FileAttributes.Directory) == 0)
            return false;
        // Cloud placeholder directories can be reparse points without redirecting the path.
        return (attributes & (uint)FileAttributes.ReparsePoint) == 0 || new DirectoryInfo(path).LinkTarget is null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string lpFileName);
}
