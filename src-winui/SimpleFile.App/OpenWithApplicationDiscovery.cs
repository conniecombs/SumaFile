using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SimpleFile.Core;

namespace SimpleFile.App;

internal static partial class OpenWithApplicationDiscovery
{
    private static readonly string[] TextExtensions =
    [
        ".cfg", ".config", ".css", ".csv", ".ini", ".json", ".jsonc", ".log", ".md",
        ".py", ".rs", ".sln", ".toml", ".ts", ".tsx", ".txt", ".xml", ".yaml", ".yml"
    ];

    private static readonly string[] ImageExtensions =
    [
        ".avif", ".bmp", ".gif", ".heic", ".jpeg", ".jpg", ".png", ".psd", ".svg", ".tif", ".tiff", ".webp"
    ];

    private static readonly string[] MediaExtensions =
    [
        ".aac", ".avi", ".flac", ".m4a", ".mkv", ".mov", ".mp3", ".mp4", ".mpeg", ".mpg", ".ogg", ".wav", ".webm", ".wmv"
    ];

    private static readonly string[] ArchiveExtensions =
    [
        ".7z", ".cab", ".gz", ".rar", ".tar", ".tgz", ".zip", ".zipx"
    ];

    private static readonly string[] DocumentExtensions =
    [
        ".doc", ".docx", ".odt", ".rtf"
    ];

    private static readonly string[] SpreadsheetExtensions =
    [
        ".csv", ".ods", ".xls", ".xlsm", ".xlsx"
    ];

    private static readonly string[] PresentationExtensions =
    [
        ".odp", ".ppt", ".pptx"
    ];

    public static IReadOnlyList<OpenWithApplication> ApplicationsForPath(string path)
    {
        var extension = OpenWithPreferences.NormalizeExtension(Path.GetExtension(path));
        if (extension == "*" || OpenWithPolicy.IsDeniedTargetExtension(extension))
        {
            return [];
        }

        var apps = new List<OpenWithApplication>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            AddRegistryApplications(extension, apps, seen);
        }
        catch
        {
        }

        AddSuggestedApplications(extension, apps, seen);

        return apps;
    }

    private static void AddRegistryApplications(
        string extension,
        List<OpenWithApplication> apps,
        HashSet<string> seen)
    {
        foreach (var command in RegistryOpenCommands(extension))
        {
            var path = ExtractExecutablePath(command);
            AddApplication(apps, seen, path, displayName: null, source: "registered");
        }

        foreach (var appName in RegistryOpenWithNames(extension))
        {
            AddApplication(apps, seen, ResolveExecutable(appName), displayName: null, source: "registered");
        }
    }

    private static IEnumerable<string> RegistryOpenCommands(string extension)
    {
        var progIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRegistryValue(progIds, Registry.CurrentUser, $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice", "ProgId");
        AddRegistryValue(progIds, Registry.CurrentUser, $@"Software\Classes\{extension}", null);
        AddRegistryValue(progIds, Registry.ClassesRoot, extension, null);

        foreach (var progId in progIds.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            foreach (var command in CommandValuesForProgId(progId))
            {
                yield return command;
            }
        }

        foreach (var command in RegistryValueCandidates(
            Registry.CurrentUser,
            $@"Software\Classes\{extension}\shell\open\command",
            null))
        {
            yield return command;
        }

        foreach (var command in RegistryValueCandidates(Registry.ClassesRoot, $@"{extension}\shell\open\command", null))
        {
            yield return command;
        }

        foreach (var command in RegistryValueCandidates(Registry.ClassesRoot, $@"SystemFileAssociations\{extension}\shell\open\command", null))
        {
            yield return command;
        }
    }

    private static IEnumerable<string> CommandValuesForProgId(string progId)
    {
        foreach (var command in RegistryValueCandidates(Registry.CurrentUser, $@"Software\Classes\{progId}\shell\open\command", null))
        {
            yield return command;
        }

        foreach (var command in RegistryValueCandidates(Registry.ClassesRoot, $@"{progId}\shell\open\command", null))
        {
            yield return command;
        }
    }

    private static IEnumerable<string> RegistryOpenWithNames(string extension)
    {
        foreach (var appName in ExplorerOpenWithList(extension))
        {
            yield return appName;
        }

        foreach (var appName in OpenWithListSubkeys(Registry.CurrentUser, $@"Software\Classes\{extension}\OpenWithList"))
        {
            yield return appName;
        }

        foreach (var appName in OpenWithListSubkeys(Registry.ClassesRoot, $@"{extension}\OpenWithList"))
        {
            yield return appName;
        }
    }

    private static IEnumerable<string> ExplorerOpenWithList(string extension)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\OpenWithList");
        if (key is null)
        {
            yield break;
        }

        var valuesByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var valueName in key.GetValueNames())
        {
            if (string.Equals(valueName, "MRUList", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (key.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value))
            {
                valuesByName[valueName] = value;
            }
        }

        if (key.GetValue("MRUList") is string mru)
        {
            foreach (var name in mru.Select(character => character.ToString()))
            {
                if (valuesByName.Remove(name, out var value))
                {
                    yield return value;
                }
            }
        }

        foreach (var value in valuesByName.Values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            yield return value;
        }
    }

    private static IEnumerable<string> OpenWithListSubkeys(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        if (key is null)
        {
            yield break;
        }

        foreach (var name in key.GetSubKeyNames().Where(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            yield return name;
        }
    }

    private static void AddSuggestedApplications(
        string extension,
        List<OpenWithApplication> apps,
        HashSet<string> seen)
    {
        foreach (var suggestion in SuggestionsFor(extension))
        {
            AddApplication(apps, seen, suggestion.Path, suggestion.DisplayName, source: "suggested");
        }
    }

    private static IEnumerable<(string DisplayName, string Path)> SuggestionsFor(string extension)
    {
        if (Matches(extension, TextExtensions))
        {
            yield return ("Visual Studio Code", PathUnderLocalPrograms(@"Microsoft VS Code\Code.exe"));
            yield return ("Visual Studio Code", PathUnderProgramFiles(@"Microsoft VS Code\Code.exe"));
            yield return ("Notepad++", PathUnderProgramFiles(@"Notepad++\notepad++.exe"));
            yield return ("Sublime Text", PathUnderProgramFiles(@"Sublime Text\sublime_text.exe"));
            yield return ("Notepad", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"));
        }

        if (Matches(extension, ImageExtensions))
        {
            yield return ("paint.net", PathUnderProgramFiles(@"paint.net\paintdotnet.exe"));
            yield return ("GIMP", PathUnderProgramFiles(@"GIMP 2\bin\gimp-2.10.exe"));
            yield return ("IrfanView", PathUnderProgramFiles(@"IrfanView\i_view64.exe"));
        }

        if (Matches(extension, MediaExtensions))
        {
            yield return ("VLC media player", PathUnderProgramFiles(@"VideoLAN\VLC\vlc.exe"));
            yield return ("MPC-HC", PathUnderProgramFiles(@"MPC-HC\mpc-hc64.exe"));
        }

        if (extension == ".pdf")
        {
            yield return ("Adobe Acrobat", PathUnderProgramFiles(@"Adobe\Acrobat DC\Acrobat\Acrobat.exe"));
            yield return ("Adobe Reader", PathUnderProgramFilesX86(@"Adobe\Acrobat Reader DC\Reader\AcroRd32.exe"));
            yield return ("SumatraPDF", PathUnderLocalPrograms(@"SumatraPDF\SumatraPDF.exe"));
            yield return ("Foxit PDF Reader", PathUnderProgramFilesX86(@"Foxit Software\Foxit PDF Reader\FoxitPDFReader.exe"));
        }

        if (Matches(extension, ArchiveExtensions))
        {
            yield return ("7-Zip", PathUnderProgramFiles(@"7-Zip\7zFM.exe"));
            yield return ("WinRAR", PathUnderProgramFiles(@"WinRAR\WinRAR.exe"));
        }

        if (Matches(extension, DocumentExtensions))
        {
            yield return ("Microsoft Word", PathUnderProgramFiles(@"Microsoft Office\root\Office16\WINWORD.EXE"));
            yield return ("LibreOffice Writer", PathUnderProgramFiles(@"LibreOffice\program\swriter.exe"));
        }

        if (Matches(extension, SpreadsheetExtensions))
        {
            yield return ("Microsoft Excel", PathUnderProgramFiles(@"Microsoft Office\root\Office16\EXCEL.EXE"));
            yield return ("LibreOffice Calc", PathUnderProgramFiles(@"LibreOffice\program\scalc.exe"));
        }

        if (Matches(extension, PresentationExtensions))
        {
            yield return ("Microsoft PowerPoint", PathUnderProgramFiles(@"Microsoft Office\root\Office16\POWERPNT.EXE"));
            yield return ("LibreOffice Impress", PathUnderProgramFiles(@"LibreOffice\program\simpress.exe"));
        }
    }

    private static bool Matches(string extension, IEnumerable<string> candidates) =>
        candidates.Contains(extension, StringComparer.OrdinalIgnoreCase);

    private static string PathUnderProgramFiles(string relativePath) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), relativePath);

    private static string PathUnderProgramFilesX86(string relativePath) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), relativePath);

    private static string PathUnderLocalPrograms(string relativePath) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            relativePath);

    private static void AddApplication(
        List<OpenWithApplication> apps,
        HashSet<string> seen,
        string? applicationPath,
        string? displayName,
        string source)
    {
        if (string.IsNullOrWhiteSpace(applicationPath))
        {
            return;
        }

        var normalizedPath = Environment.ExpandEnvironmentVariables(applicationPath.Trim());
        if (!IsLaunchableApplication(normalizedPath) || !seen.Add(normalizedPath))
        {
            return;
        }

        apps.Add(OpenWithApplication.FromPath(
            normalizedPath,
            string.IsNullOrWhiteSpace(displayName) ? DisplayNameFor(normalizedPath) : displayName,
            source));
    }

    private static string? ResolveExecutable(string application)
    {
        var trimmed = application.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(trimmed);
        if (Path.IsPathFullyQualified(expanded))
        {
            return expanded;
        }

        var fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".com", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".exe";
        }

        return ResolveFromAppPaths(fileName)
            ?? ResolveFromApplicationCommand(fileName);
    }

    private static string? ResolveFromAppPaths(string fileName)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var path in RegistryValueCandidates(root, $@"Software\Microsoft\Windows\CurrentVersion\App Paths\{fileName}", null))
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
        }

        using var localMachine32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        foreach (var path in RegistryValueCandidates(localMachine32, $@"Software\Microsoft\Windows\CurrentVersion\App Paths\{fileName}", null))
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string? ResolveFromApplicationCommand(string fileName)
    {
        foreach (var command in RegistryValueCandidates(Registry.CurrentUser, $@"Software\Classes\Applications\{fileName}\shell\open\command", null))
        {
            var path = ExtractExecutablePath(command);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        foreach (var command in RegistryValueCandidates(Registry.ClassesRoot, $@"Applications\{fileName}\shell\open\command", null))
        {
            var path = ExtractExecutablePath(command);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        return null;
    }

    private static IEnumerable<string> RegistryValueCandidates(RegistryKey root, string path, string? valueName)
    {
        using var key = root.OpenSubKey(path);
        if (key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value))
        {
            yield return value;
        }
    }

    private static void AddRegistryValue(HashSet<string> values, RegistryKey root, string path, string? valueName)
    {
        using var key = root.OpenSubKey(path);
        if (key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value))
        {
            values.Add(value);
        }
    }

    internal static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        var match = ExecutableCommandPattern().Match(expanded);
        return match.Success ? match.Groups["path"].Value.Trim() : null;
    }

    [GeneratedRegex("""^\s*(?:"(?<path>[^"]+\.(?:exe|com))"|(?<path>.*?\.(?:exe|com)))(?:\s|$)""", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutableCommandPattern();

    private static bool IsLaunchableApplication(string applicationPath) =>
        OpenWithPolicy.IsLaunchableApplication(applicationPath);

    private static string DisplayNameFor(string applicationPath)
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(applicationPath);
            if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription))
            {
                return versionInfo.FileDescription;
            }

            if (!string.IsNullOrWhiteSpace(versionInfo.ProductName))
            {
                return versionInfo.ProductName;
            }
        }
        catch
        {
        }

        return Path.GetFileNameWithoutExtension(applicationPath);
    }
}
