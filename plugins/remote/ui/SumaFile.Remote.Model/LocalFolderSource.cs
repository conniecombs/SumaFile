using System.Globalization;

namespace SumaFile.Remote.Model;

/// <summary>
/// Lists a local folder with <see cref="System.IO"/> for the manager's local pane: folders first, then names
/// (ordinal, ignoring case); <c>Modified</c> as RFC 3339 UTC; <c>Parent</c> from <see cref="DirectoryInfo.Parent"/>.
/// </summary>
public sealed class LocalFolderSource : ILocalFolderSource
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public Task<RemoteDirectoryListing> ListAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(() => List(path, ct), ct);
    }

    private static RemoteDirectoryListing List(string path, CancellationToken ct)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path.Trim()));
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"Folder not found: {directory.FullName}");
        }

        var entries = new List<RemoteFileEntry>();
        foreach (var info in directory.EnumerateFileSystemInfos("*", Options))
        {
            ct.ThrowIfCancellationRequested();
            entries.Add(ToEntry(info));
        }

        var fullPath = directory.FullName;
        return new RemoteDirectoryListing
        {
            Path = Path.GetPathRoot(fullPath) == fullPath ? fullPath : Path.TrimEndingDirectorySeparator(fullPath),
            Parent = directory.Parent?.FullName,
            Entries = entries
                .OrderBy(entry => entry.IsDir ? 0 : 1)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            IsNetwork = false,
        };
    }

    private static RemoteFileEntry ToEntry(FileSystemInfo info)
    {
        var attributes = info.Attributes;
        var isDir = (attributes & FileAttributes.Directory) != 0;
        string? linkTarget = null;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            try { linkTarget = info.LinkTarget; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        return new RemoteFileEntry
        {
            Name = info.Name,
            Path = info.FullName,
            IsDir = isDir,
            IsSymlink = linkTarget is not null,
            IsHidden = (attributes & FileAttributes.Hidden) != 0,
            IsSystem = (attributes & FileAttributes.System) != 0,
            Size = !isDir && info is FileInfo file ? (ulong)file.Length : 0,
            Modified = info.LastWriteTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Extension = isDir ? "" : ExtensionOf(info.Name),
            SymlinkTarget = linkTarget,
        };
    }

    /// <summary>The text after the last dot, as the service reports it: none for <c>.profile</c> or <c>name.</c>.</summary>
    private static string ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..] : "";
    }
}
