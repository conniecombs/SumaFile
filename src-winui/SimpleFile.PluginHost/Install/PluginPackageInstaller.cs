using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SimpleFile.PluginHost.Manifest;
using SimpleFile.PluginHost.Trust;

namespace SimpleFile.PluginHost.Install;

/// <summary>A <c>.sfplugin</c> package could not be installed; the plugin root is left as it was.</summary>
public sealed class PluginInstallException : Exception
{
    public PluginInstallException(string message) : base(message) { }

    public PluginInstallException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Installs <c>.sfplugin</c> packages (zip files with <c>plugin.json</c> at the root) into SumaFile's plugins folder,
/// <c>&lt;install&gt;\plugins</c>, beside the bundled plugins, and removes them again. Plugins that folder's
/// <c>bundled.json</c> lists ship with SumaFile: a package never replaces one and Remove never deletes one.
/// </summary>
public static class PluginPackageInstaller
{
    private const string ManifestFileName = "plugin.json";

    /// <summary>
    /// Validates the package (a valid root manifest whose id <c>bundled.json</c> does not list, every entry inside
    /// <c>&lt;pluginsRoot&gt;\&lt;id&gt;\</c>), extracts it into a staging folder next to the target and moves it into
    /// place, replacing an existing folder. Returns the installed folder. Throws <see cref="PluginInstallException"/>
    /// without writing anything when validation fails, and explains an unwritable plugins folder.
    /// </summary>
    public static string Install(string sfpluginPath, string pluginsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sfpluginPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginsRoot));

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
            if (BundledPluginList.Read(root).ContainsKey(id))
            {
                // Trust is keyed on bundled.json: a package must never replace a plugin that ships with SumaFile.
                throw new PluginInstallException($"{id} ships with SumaFile and cannot be installed from a package.");
            }

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

            var staging = Path.Combine(root, $".{id}.installing-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(staging);
            }
            catch (UnauthorizedAccessException ex)
            {
                // An install folder the user cannot write to (a portable copy under Program Files, an admin-protected
                // custom folder). Nothing falls back to another folder.
                throw Unwritable(root, ex);
            }
            catch (IOException ex)
            {
                TryDelete(staging);
                throw new PluginInstallException($"The plugin could not be installed: {ex.Message}", ex);
            }

            try
            {
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
    /// The record is a plugin the user installed: it sits in <paramref name="pluginsRoot"/> and its id is not one that
    /// <c>bundled.json</c> lists (<see cref="PluginRecord.IsBundledId"/>). Settings offers Remove only for these.
    /// </summary>
    public static bool IsRemovable(PluginRecord record, string pluginsRoot)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.IsBundledId || string.IsNullOrWhiteSpace(pluginsRoot)) return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(record.Discovered.Root)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginsRoot)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes a plugin folder the user installed, which must be a direct child of <paramref name="pluginsRoot"/> whose
    /// id that root's <c>bundled.json</c> does not list (plugins that ship with SumaFile are never removed). Its settings
    /// and its <c>plugins-data</c> folder are kept. The folder is renamed aside first, so a plugin whose files are in
    /// use fails cleanly instead of being half deleted.
    /// </summary>
    public static void Remove(string pluginFolder, string pluginsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginsRoot));
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginFolder));
        var parent = Path.GetDirectoryName(folder);
        var name = Path.GetFileName(folder);
        if (parent is null
            || !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase)
            || name.Length == 0
            || name.StartsWith('.'))
        {
            throw new PluginInstallException("Only plugins in SumaFile's plugins folder can be removed.");
        }

        if (BundledPluginList.Read(root).Keys.Any(id => string.Equals(id, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PluginInstallException($"{name} ships with SumaFile and cannot be removed. Disable it instead.");
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

        if (!TryDelete(removing))
        {
            // Already out of discovery's sight (dot-prefixed); only its files are left behind.
            throw new PluginInstallException(
                $"The plugin {name} was removed, but some of its files are still in use. Restart SumaFile to finish removing them.");
        }
    }

    private static PluginInstallException Unwritable(string root, UnauthorizedAccessException ex) =>
        new($"SumaFile cannot write to its plugins folder {root}: {ex.Message}", ex);

    private static string ReadManifestId(ZipArchive zip)
    {
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, ManifestFileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new PluginInstallException("The package has no plugin.json at its root.");
        // The cap discovery applies (parked #28). The declared size is checked first; the read itself is bounded too,
        // so an entry whose header understates its size is never inflated past the cap.
        const string tooLarge = "The package's " + PluginManifestReader.ManifestTooLargeMessage;
        if (entry.Length > PluginManifestReader.MaxManifestBytes)
        {
            throw new PluginInstallException(tooLarge);
        }

        string json;
        try
        {
            byte[] bytes;
            using (var stream = entry.Open())
            {
                if (!PluginManifestReader.TryReadManifestBytes(stream, out bytes))
                {
                    throw new PluginInstallException(tooLarge);
                }
            }

            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8);
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

    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Deletes one of the installer's own staging, replaced or removing folders, trying once more after a short
    /// delay (antivirus scanners and the indexer often hold freshly written files briefly). Returns false when the
    /// folder is still there; discovery ignores such dot-prefixed leftovers.
    /// </summary>
    private static bool TryDelete(string folder)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 1)
                {
                    return false;
                }

                Thread.Sleep(DeleteRetryDelay);
            }
        }
    }
}
