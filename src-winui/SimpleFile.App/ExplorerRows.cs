using Microsoft.UI.Xaml;
using SimpleFile.Core;
using SimpleFile.Ipc;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.App;

public sealed class FileRow
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsDir { get; set; }
    public string Icon { get; set; } = "";
    public string SizeText { get; set; } = "";
    public string ItemsText { get; set; } = "";
    public string ModifiedText { get; set; } = "";
    public string TypeText { get; set; } = "";
    public bool IsCut { get; set; }
    public ulong Size { get; set; }
    public string Extension { get; set; } = "";
    public string ExtensionText { get; set; } = "";
    public string GitText { get; set; } = "";
    public string SymlinkText { get; set; } = "";
    public string PathText { get; set; } = "";
    public string ParentText { get; set; } = "";
    public string TagColor { get; set; } = "";
    public string TagName { get; set; } = "";
    public PaneId Pane { get; set; } = PaneId.Primary;
    public bool IsHidden { get; set; }
    public string AutomationName => IsDir ? $"Folder {Name}" : $"File {Name}";

    public string ColumnText(string columnId)
    {
        return columnId switch
        {
            "name" => Name,
            "size" => SizeText,
            "items" => ItemsText,
            "date" or "modified" => ModifiedText,
            "type" => TypeText,
            "extension" => ExtensionText,
            "git" => GitText,
            "symlink" => SymlinkText,
            "path" => PathText,
            "parent" => ParentText,
            _ => "",
        };
    }

    public static FileRow From(FileEntry entry, bool isCut = false, Tag? tag = null, PaneId pane = PaneId.Primary)
    {
        return new FileRow
        {
            Name = entry.Name,
            Path = entry.Path,
            IsDir = entry.IsDir,
            Icon = EntryPresentation.EntryIcon(entry),
            SizeText = EntryPresentation.ColumnText(entry, "size"),
            ItemsText = EntryPresentation.ColumnText(entry, "items"),
            ModifiedText = EntryPresentation.FormatModified(entry.Modified),
            TypeText = EntryPresentation.FileType(entry),
            IsCut = isCut,
            Size = entry.Size,
            Extension = entry.Extension ?? "",
            ExtensionText = EntryPresentation.ColumnText(entry, "extension"),
            GitText = entry.GitStatus ?? "",
            SymlinkText = entry.SymlinkTarget ?? "",
            PathText = entry.Path,
            ParentText = EntryPresentation.ColumnText(entry, "parent"),
            TagColor = tag?.Color ?? "",
            TagName = tag?.Name ?? "",
            Pane = pane,
            IsHidden = EntryPresentation.IsHiddenFromUser(entry),
        };
    }
}

public sealed class DriveRow
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Badge { get; set; } = "";
    public string Description { get; set; } = "";
    public string UsageText { get; set; } = "";
    public double UsedPercent { get; set; }
    public bool ShowUsage { get; set; }
    public bool IsActive { get; set; }
    public DriveInfo Source { get; set; } = new();
    public Visibility UsageVisibility => ShowUsage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeVisibility => string.IsNullOrWhiteSpace(Badge) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DescriptionVisibility => string.IsNullOrWhiteSpace(Description) ? Visibility.Collapsed : Visibility.Visible;
    public double RowOpacity => Badge.Equals("Offline", StringComparison.OrdinalIgnoreCase) ? 0.62 : 1.0;
    public string AutomationName
    {
        get
        {
            var details = new[] { Name, Path, Badge, Description, UsageText }
                .Where(part => !string.IsNullOrWhiteSpace(part));
            return string.Join(", ", details);
        }
    }

    public static DriveRow From(DriveInfo drive, string currentPath)
    {
        var usedPercent = 0d;
        var showUsage = drive.TotalSpace > 0;
        if (showUsage)
        {
            var used = drive.TotalSpace > drive.FreeSpace ? drive.TotalSpace - drive.FreeSpace : 0;
            usedPercent = 100d * used / drive.TotalSpace;
        }

        return new DriveRow
        {
            Name = string.IsNullOrEmpty(drive.Name) ? drive.Path : drive.Name,
            Path = drive.Path,
            Icon = DrivePresentation.Icon(drive),
            Badge = DrivePresentation.Badge(drive),
            Description = DrivePresentation.Description(drive),
            UsageText = showUsage
                ? $"{EntryPresentation.FormatFileSize(drive.FreeSpace)} free"
                : "",
            UsedPercent = usedPercent,
            ShowUsage = showUsage,
            IsActive = PathRules.PathContains(drive.Path, currentPath)
                || PathRules.PathsEqual(drive.Path, currentPath),
            Source = drive,
        };
    }
}

public sealed class QuickAccessRow
{
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Command { get; set; } = "";
    public string Path { get; set; } = "";

    public static string ResolvePath(string command, string homePath)
    {
        try
        {
            var paths = Windows.Storage.UserDataPaths.GetDefault();
            return command switch
            {
                "navigateHome" => string.IsNullOrWhiteSpace(homePath) ? paths.Profile : homePath,
                "navigateDesktop" => paths.Desktop,
                "navigateDocuments" => paths.Documents,
                "navigateDownloads" => paths.Downloads,
                "navigatePictures" => paths.Pictures,
                "navigateRecycleBin" => PathRules.RecycleBinPath,
                _ => homePath,
            };
        }
        catch
        {
            return command switch
            {
                "navigateHome" => homePath,
                "navigateDesktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "navigateDocuments" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "navigateDownloads" => System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads"),
                "navigatePictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "navigateRecycleBin" => PathRules.RecycleBinPath,
                _ => homePath,
            };
        }
    }
}
