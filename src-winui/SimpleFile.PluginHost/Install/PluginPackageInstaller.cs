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

/// <summary>The caps <see cref="PluginPackageInstaller"/> applies to a package (tests pass smaller ones).</summary>
internal readonly record struct PackageLimits(int MaxEntries, long MaxUncompressedBytes)
{
    public static PackageLimits Default => new(PluginPackageInstaller.MaxEntries, PluginPackageInstaller.MaxUncompressedBytes);
}

/// <summary>
/// Installs <c>.sfplugin</c> packages (zip files with <c>plugin.json</c> at the root) into SumaFile's plugins folder,
/// <c>&lt;install&gt;\plugins</c>, beside the bundled plugins, and removes them again. Plugins that folder's
/// <c>bundled.json</c> lists ship with SumaFile: a package never replaces one and Remove never deletes one.
/// </summary>
public static class PluginPackageInstaller
{
    private const string ManifestFileName = "plugin.json";

    /// <summary>The most entries (files and folders) a package may hold.</summary>
    public const int MaxEntries = 10_000;

    /// <summary>The most bytes a package's entries may inflate to, all together: 512 MiB.</summary>
    public const long MaxUncompressedBytes = 512L * 1024 * 1024;

    /// <inheritdoc cref="Install(string, string, Action{string}?)"/>
    public static string Install(string sfpluginPath, string pluginsRoot) => Install(sfpluginPath, pluginsRoot, null);

    /// <summary>
    /// Validates the package (a valid root manifest whose id <c>bundled.json</c> does not list, at most
    /// <see cref="MaxEntries"/> entries, every entry inside <c>&lt;pluginsRoot&gt;\&lt;id&gt;\</c> with a name Windows can
    /// keep), extracts it into a staging folder next to the target, never past <see cref="MaxUncompressedBytes"/>
    /// whatever the entries declare, and moves it into place, replacing an existing folder. Returns the installed
    /// folder. Throws <see cref="PluginInstallException"/> without writing anything when validation fails, and explains
    /// an unwritable plugins folder.
    /// </summary>
    /// <param name="beforeMove">
    /// Called with the plugin id once the package is staged, before anything in the target folder changes;
    /// <see cref="PluginManager.InstallPackageAsync"/> clears the plugin's stored approval there, so the installed code
    /// needs consent again. If it throws, the staging folder is deleted and the previous version stays.
    /// </param>
    public static string Install(string sfpluginPath, string pluginsRoot, Action<string>? beforeMove) =>
        Install(sfpluginPath, pluginsRoot, beforeMove, PackageLimits.Default);

    internal static string Install(string sfpluginPath, string pluginsRoot, Action<string>? beforeMove, PackageLimits limits)
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
            // Counted before anything else reads the entries (the central directory is already parsed, but bounded by
            // the file's size); a declared size is only a first check, the copy below enforces the real one.
            if (zip.Entries.Count > limits.MaxEntries)
            {
                throw new PluginInstallException(
                    $"The package has more than {limits.MaxEntries.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} entries.");
            }

            var tooLarge = $"The package is larger than {FormatSize(limits.MaxUncompressedBytes)} uncompressed.";
            if (zip.Entries.Sum(entry => entry.Length) > limits.MaxUncompressedBytes)
            {
                throw new PluginInstallException(tooLarge);
            }

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

                var remaining = limits.MaxUncompressedBytes;
                foreach (var (entry, relative) in files)
                {
                    var destination = Contained(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    remaining -= ExtractBounded(entry, destination, remaining, tooLarge);
                }

                beforeMove?.Invoke(id);
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
    public static void Remove(string pluginFolder, string pluginsRoot) => Remove(pluginFolder, pluginsRoot, null);

    /// <inheritdoc cref="Remove(string, string)"/>
    /// <param name="beforeRemove">
    /// Called with the plugin id once the folder is known to be removable, before anything changes;
    /// <see cref="PluginManager.RemovePackageAsync"/> clears the plugin's stored approval there, so a later install of
    /// the same id needs consent again. If it throws, nothing is removed.
    /// </param>
    public static void Remove(string pluginFolder, string pluginsRoot, Action<string>? beforeRemove)
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

        if (beforeRemove is not null)
        {
            try
            {
                beforeRemove(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PluginInstallException($"The plugin {name} could not be removed: {ex.Message}", ex);
            }
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

        var kept = segments.Where(segment => segment != ".").ToArray();
        foreach (var segment in kept)
        {
            // Win32 trims a trailing dot or space and opens a device for a reserved name, so such an entry would not
            // land where its name says (or at all).
            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                throw new PluginInstallException(
                    $"The package entry '{entryName}' has a name that ends in a dot or a space, which Windows cannot keep.");
            }

            if (IsDeviceName(segment))
            {
                throw new PluginInstallException($"The package entry '{entryName}' uses a name Windows reserves for a device.");
            }
        }

        var relative = string.Join(Path.DirectorySeparatorChar, kept);
        if (relative.Length == 0)
        {
            return "";
        }

        Contained(target, relative, entryName);
        return relative;
    }

    private static readonly string[] DeviceNames = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"];

    /// <summary>
    /// CON, PRN, AUX, NUL, CONIN$, CONOUT$, COM0-9 and LPT0-9 (the 0 and superscript ¹²³ forms are in Windows' list too),
    /// with any extension, in any case; Win32 ignores trailing spaces before the extension, so they are ignored too.
    /// </summary>
    internal static bool IsDeviceName(string segment)
    {
        var dot = segment.IndexOf('.');
        var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        if (DeviceNames.Any(device => string.Equals(stem, device, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is (>= '0' and <= '9') or '\u00B9' or '\u00B2' or '\u00B3';
    }

    /// <summary>Copies the entry to a new file with <see cref="CopyBounded"/> and returns how many bytes it wrote.</summary>
    private static long ExtractBounded(ZipArchiveEntry entry, string destination, long budget, string tooLarge)
    {
        using var input = entry.Open();
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return CopyBounded(input, output, budget, tooLarge);
    }

    /// <summary>
    /// Copies <paramref name="input"/> to <paramref name="output"/>, reading at most <paramref name="budget"/> + 1 bytes
    /// whatever the zip entry behind it declares, and returns how many it wrote. Throws <paramref name="tooLarge"/> once
    /// the input passes the budget; nothing past the budget is written.
    /// </summary>
    internal static long CopyBounded(Stream input, Stream output, long budget, string tooLarge)
    {
        var buffer = new byte[81920];
        long written = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, budget - written + 1));
            if (read == 0)
            {
                return written;
            }

            written += read;
            if (written > budget)
            {
                throw new PluginInstallException(tooLarge);
            }

            output.Write(buffer, 0, read);
        }
    }

    private static string FormatSize(long bytes) =>
        bytes % (1024 * 1024) == 0 ? $"{bytes / (1024 * 1024)} MiB"
        : bytes % 1024 == 0 ? $"{bytes / 1024} KiB"
        : $"{bytes} bytes";

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
