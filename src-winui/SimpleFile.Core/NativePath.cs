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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string lpFileName);
}
