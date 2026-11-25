namespace SimpleFile.Core;

public enum OmnibarSuggestionKind
{
    /// <summary>A folder: accepting it fills the box with its path and a separator, ready for the next name.</summary>
    Folder,

    /// <summary>A command: accepting it runs the command.</summary>
    Command,
}

/// <summary>
/// One line of the list under the omnibar or under a pane's path box. <paramref name="Value"/> is the folder's path
/// or the command's id; <paramref name="Text"/> is what the line shows.
/// </summary>
public sealed record OmnibarSuggestion(OmnibarSuggestionKind Kind, string Text, string Value);

/// <summary>
/// What a text asks suggestions for: the folders of <paramref name="Directory"/> whose names start with
/// <paramref name="Prefix"/>, the commands that match <paramref name="Query"/>, or nothing.
/// </summary>
public sealed record OmnibarSuggestionRequest(OmnibarSuggestionKind? Kind, string Directory = "", string Prefix = "", string Query = "")
{
    public static OmnibarSuggestionRequest None { get; } = new((OmnibarSuggestionKind?)null);
}

/// <summary>
/// The suggestions offered while typing in the omnibar and in a pane's path box. A text is read the way
/// <see cref="OmnibarIntentParser"/> reads it when it is run, so the list offers what Enter would act on: folders
/// for a path, commands after <c>&gt;</c> or in command mode, and nothing for a search or a filter, whose results
/// are the file list itself.
/// </summary>
public static class OmnibarSuggestions
{
    public const int MaxFolders = 12;
    public const int MaxCommands = 8;

    public static OmnibarSuggestionRequest Request(string? text, OmnibarMode mode, string? currentPath)
    {
        var value = (text ?? "").TrimStart();
        if (value.Length == 0)
        {
            return OmnibarSuggestionRequest.None;
        }

        if (value[0] == '>')
        {
            return Commands(value[1..]);
        }

        if (value[0] == '?' || (value[0] == '~' && !(value.Length > 1 && value[1] is '\\' or '/')))
        {
            return OmnibarSuggestionRequest.None;
        }

        return mode switch
        {
            OmnibarMode.Command => Commands(value),
            OmnibarMode.Navigate => Folders(value, currentPath ?? ""),
            _ => OmnibarSuggestionRequest.None,
        };
    }

    /// <summary>The folders a typed path asks for; a pane's path box always holds a path.</summary>
    public static OmnibarSuggestionRequest Folders(string? typed, string currentPath)
    {
        var value = (typed ?? "").Trim();
        if (PathCompletion.TrySplit(value, out var directory, out var prefix))
        {
            return new OmnibarSuggestionRequest(OmnibarSuggestionKind.Folder, directory, prefix);
        }

        // A bare name is a folder of the one being shown, as it is when it is run.
        var bare = value.Length > 0 && value.IndexOfAny(['\\', '/', ':']) < 0;
        return bare && currentPath.Length > 0 && !PathRules.IsRecycleBinPath(currentPath)
            ? new OmnibarSuggestionRequest(OmnibarSuggestionKind.Folder, currentPath, value)
            : OmnibarSuggestionRequest.None;
    }

    private static OmnibarSuggestionRequest Commands(string query)
    {
        var needle = query.Trim();
        return needle.Length == 0
            ? OmnibarSuggestionRequest.None
            : new OmnibarSuggestionRequest(OmnibarSuggestionKind.Command, Query: needle);
    }

    /// <summary>The folders among <paramref name="candidatePaths"/> whose names start with <paramref name="prefix"/>.</summary>
    public static IReadOnlyList<OmnibarSuggestion> FolderSuggestions(IEnumerable<string> candidatePaths, string prefix) =>
        PathCompletion.Suggest(candidatePaths, prefix, MaxFolders)
            .Select(path => new OmnibarSuggestion(OmnibarSuggestionKind.Folder, path, path))
            .ToList();

    /// <summary>
    /// The commands that match <paramref name="query"/>, best first: a label that starts with it, then one with a
    /// word that starts with it, then any other match of the label, the id or the group.
    /// </summary>
    public static IReadOnlyList<OmnibarSuggestion> CommandSuggestions(string query, IEnumerable<AppCommand>? commands = null)
    {
        var needle = (query ?? "").Trim();
        if (needle.Length == 0)
        {
            return [];
        }

        return (commands ?? AppCommandCatalog.Filter(needle))
            .Select((command, index) => (command, index, rank: Rank(command, needle)))
            .Where(item => item.rank < 3)
            .OrderBy(item => item.rank)
            .ThenBy(item => item.index)
            .Take(MaxCommands)
            .Select(item => new OmnibarSuggestion(OmnibarSuggestionKind.Command, $"{item.command.Label}  ({item.command.Group})", item.command.Id))
            .ToList();
    }

    private static int Rank(AppCommand command, string needle)
    {
        if (command.Label.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (command.Label.Contains(" " + needle, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return command.Label.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || command.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || command.Group.Contains(needle, StringComparison.OrdinalIgnoreCase)
            ? 2
            : 3;
    }

    /// <summary>What accepting a folder puts in the box: its path with a separator, so typing goes on inside it.</summary>
    public static string AcceptedFolderText(string path) =>
        path.TrimEnd('\\', '/') + PathRules.PathSeparator(path);

    /// <summary>
    /// The line Down or Up moves to from <paramref name="current"/> (-1 for none) in a list of
    /// <paramref name="count"/>. Past either end is no line again, which is the typed text.
    /// </summary>
    public static int MoveSelection(int current, int count, int delta)
    {
        if (count <= 0)
        {
            return -1;
        }

        var next = Math.Clamp(current, -1, count - 1) + Math.Sign(delta);
        return next < -1 ? count - 1 : next >= count ? -1 : next;
    }
}
