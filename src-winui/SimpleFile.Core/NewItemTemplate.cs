using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed record NewItemTemplate(string Id, string Label, string DefaultName, bool IsDirectory)
{
    public static NewItemTemplate Folder { get; } = new("folder", "Folder", "New Folder", IsDirectory: true);
    public static NewItemTemplate TextFile { get; } = new("text", "Text file", "New Text Document.txt", IsDirectory: false);
    public static NewItemTemplate MarkdownFile { get; } = new("markdown", "Markdown file", "New Markdown Document.md", IsDirectory: false);
    public static NewItemTemplate JsonFile { get; } = new("json", "JSON file", "New JSON Document.json", IsDirectory: false);
    public static NewItemTemplate EmptyFile { get; } = new("empty", "Empty file", "New File", IsDirectory: false);

    public static NewItemTemplate? Find(string id)
    {
        var normalized = id.StartsWith("new:", StringComparison.Ordinal)
            ? id["new:".Length..]
            : id;
        return normalized switch
        {
            "folder" => Folder,
            "text" => TextFile,
            "markdown" => MarkdownFile,
            "json" => JsonFile,
            "empty" => EmptyFile,
            _ => null,
        };
    }

    public string SuggestedName(IEnumerable<FileEntry> entries)
    {
        var used = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return UniqueName(DefaultName, used);
    }

    public static int RenameSelectionLength(string name, bool isDirectory)
    {
        if (isDirectory)
        {
            return name.Length;
        }

        var extensionStart = ExtensionStart(name);
        return extensionStart > 0 ? extensionStart : name.Length;
    }

    private static string UniqueName(string defaultName, IReadOnlySet<string> used)
    {
        if (!used.Contains(defaultName))
        {
            return defaultName;
        }

        var (stem, extension) = SplitStemAndExtension(defaultName);
        for (var index = 2; index < 10_000; index++)
        {
            var candidate = $"{stem} ({index}){extension}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{stem} {Guid.NewGuid():N}{extension}";
    }

    private static (string Stem, string Extension) SplitStemAndExtension(string name)
    {
        var extensionStart = ExtensionStart(name);
        return extensionStart > 0
            ? (name[..extensionStart], name[extensionStart..])
            : (name, "");
    }

    private static int ExtensionStart(string name)
    {
        var index = name.LastIndexOf('.');
        return index <= 0 || index == name.Length - 1 ? -1 : index;
    }
}
