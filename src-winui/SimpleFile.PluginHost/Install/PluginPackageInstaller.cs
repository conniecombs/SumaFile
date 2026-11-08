using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SimpleFile.PluginHost.Manifest;

namespace SimpleFile.PluginHost.Install;

/// <summary>A <c>.sfplugin</c> package could not be installed; the plugin root is left as it was.</summary>
public sealed class PluginInstallException : Exception
{
    public PluginInstallException(string message) : base(message) { }

    public PluginInstallException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Installs <c>.sfplugin</c> packages (zip files with <c>plugin.json</c> at the root) into a plugin root.</summary>
public static class PluginPackageInstaller
{
    private const string ManifestFileName = "plugin.json";
    private const long MaxManifestBytes = 1_048_576;

    /// <summary>
    /// Validates the package (a valid root manifest, every entry inside <c>&lt;userRoot&gt;\&lt;id&gt;\</c>), extracts it
    /// into a staging folder next to the target and moves it into place, replacing an existing folder. Returns the
    /// installed folder. Throws <see cref="PluginInstallException"/> without writing anything when validation fails.
    /// </summary>
    public static string Install(string sfpluginPath, string userRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sfpluginPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userRoot));

        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(sfpluginPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new PluginInstallException($"The file is not a readable plugin package: {ex.Message}", ex);
        }

        using (zip)
        {
            var id = ReadManifestId(zip);
            var target = Path.Combine(root, id);

            // Validate every entry before anything touches the disk.
            var files = new List<(ZipArchiveEntry Entry, string Relative)>();
            var directories = new List<string>();
            foreach (var entry in zip.Entries)
            {
                var relative = SafeRelativePath(entry.FullName, target);
                if (relative.Length == 0)
                {
                    continue;
                }

                if (IsDirectoryEntry(entry))
                {
                    directories.Add(relative);
                }
                else
                {
                    files.Add((entry, relative));
                }
            }

            Directory.CreateDirectory(root);
            var staging = Path.Combine(root, $".{id}.installing-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(staging);
                foreach (var directory in directories)
                {
                    Directory.CreateDirectory(Contained(staging, directory));
                }

                foreach (var (entry, relative) in files)
                {
                    var destination = Contained(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: false);
                }

                MoveIntoPlace(staging, target, root, id);
            }
            catch (Exception ex)
            {
                TryDelete(staging);
                if (ex is PluginInstallException)
                {
                    throw;
                }

                throw new PluginInstallException($"The plugin could not be installed: {ex.Message}", ex);
            }

            return target;
        }
    }

    /// <summary>
    /// Deletes an installed plugin folder, which must be a direct child of <paramref name="userRoot"/> (plugins
    /// shipped in the install root are never removed). The folder is renamed aside first, so a plugin whose files
    /// are in use fails cleanly instead of being half deleted.
    /// </summary>
    public static void Remove(string pluginFolder, string userRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(userRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userRoot));
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginFolder));
        var parent = Path.GetDirectoryName(folder);
        var name = Path.GetFileName(folder);
        if (parent is null
            || !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase)
            || name.Length == 0
            || name.StartsWith('.'))
        {
            throw new PluginInstallException("Only plugins installed in your user plugins folder can be removed.");
        }

        if (!Directory.Exists(folder))
        {
            return;
        }

        var removing = Path.Combine(root, $".{name}.removing-{Guid.NewGuid():N}");
        try
        {
            Directory.Move(folder, removing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginInstallException(
                $"The plugin {name} is in use and could not be removed. Disable it, restart SumaFile and try again. ({ex.Message})",
                ex);
        }

        TryDelete(removing);
    }

    private static string ReadManifestId(ZipArchive zip)
    {
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, ManifestFileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new PluginInstallException("The package has no plugin.json at its root.");
        if (entry.Length > MaxManifestBytes)
        {
            throw new PluginInstallException("The package's plugin.json is too large.");
        }

        string json;
        try
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            json = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new PluginInstallException($"The package's plugin.json could not be read: {ex.Message}", ex);
        }

        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("id", out var idElement)
                && idElement.ValueKind == JsonValueKind.String)
            {
                id = idElement.GetString();
            }
        }
        catch (JsonException)
        {
            // The reader below reports the parse error.
        }

        // The id names the target folder, so the reader's folder-name check holds by construction; the reader still
        // enforces the id pattern, which keeps the id a single safe path segment.
        var result = PluginManifestReader.Read(json, id ?? "");
        if (!result.IsValid)
        {
            throw new PluginInstallException("The package's plugin.json is invalid: " + string.Join(" ", result.Errors));
        }

        return result.Manifest!.Id;
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    /// <summary>
    /// Normalises an entry name to a relative path and proves it stays under <paramref name="target"/>.
    /// Returns "" for an entry that names the folder itself.
    /// </summary>
    private static string SafeRelativePath(string entryName, string target)
    {
        var normalized = entryName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || normalized.Contains(':'))
        {
            throw new PluginInstallException($"The package entry '{entryName}' uses an absolute path.");
        }

        var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            throw new PluginInstallException($"The package entry '{entryName}' points outside the plugin folder.");
        }

        var relative = string.Join(Path.DirectorySeparatorChar, segments.Where(segment => segment != "."));
        if (relative.Length == 0)
        {
            return "";
        }

        Contained(target, relative, entryName);
        return relative;
    }

    private static string Contained(string folder, string relative, string? entryName = null)
    {
        var full = Path.GetFullPath(Path.Combine(folder, relative));
        if (!full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginInstallException($"The package entry '{entryName ?? relative}' points outside the plugin folder.");
        }

        return full;
    }

    /// <summary>
    /// Renames any existing folder aside, moves the staged folder in, then deletes the old one. If the move fails
    /// the old folder is put back, so a failed install leaves the previous version in place.
    /// </summary>
    private static void MoveIntoPlace(string staging, string target, string root, string id)
    {
        if (!Directory.Exists(target))
        {
            Directory.Move(staging, target);
            return;
        }

        var backup = Path.Combine(root, $".{id}.replaced-{Guid.NewGuid():N}");
        try
        {
            Directory.Move(target, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginInstallException(
                $"The installed copy of {id} is in use and could not be replaced. Disable it, restart SumaFile and try again. ({ex.Message})",
                ex);
        }

        try
        {
            Directory.Move(staging, target);
        }
        catch
        {
            Directory.Move(backup, target);
            throw;
        }

        TryDelete(backup);
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a staging or replaced folder.
        }
    }
}
