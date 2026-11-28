using System.Collections.Concurrent;

namespace SimpleFile.Core;

/// <summary>
/// Says whether a path is on a network drive: a share named by its server (<c>\\server\share</c>), or a drive
/// letter Windows maps to one. Video thumbnails from such a drive queue apart from local ones.
/// </summary>
public sealed class NetworkPaths
{
    private readonly Func<string, DriveType> _driveTypeOf;
    private readonly ConcurrentDictionary<string, bool> _networkRoots = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="driveTypeOf">
    /// What kind of drive a root such as <c>Z:\</c> is. Windows is asked when none is given.
    /// </param>
    public NetworkPaths(Func<string, DriveType>? driveTypeOf = null)
    {
        _driveTypeOf = driveTypeOf ?? (root => new DriveInfo(root).DriveType);
    }

    /// <summary>The one the app uses. It remembers each drive it has asked Windows about.</summary>
    public static NetworkPaths Shared { get; } = new();

    public bool IsNetwork(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var text = path.Trim().Replace('/', '\\');
        if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (text.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            text = text[4..];
        }
        else if (text.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        if (text.Length < 2 || text[1] != ':' || !char.IsAsciiLetter(text[0]))
        {
            return false;
        }

        // A drive is asked about once: the answer does not change while the app runs often enough to matter,
        // and this is asked for every video row.
        return _networkRoots.GetOrAdd(text[..2] + @"\", root =>
        {
            try
            {
                return _driveTypeOf(root) == DriveType.Network;
            }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // A drive Windows cannot describe: treated as local.
                return false;
            }
        });
    }
}
