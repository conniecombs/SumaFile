namespace SimpleFile.Core;

public static class ContextMenuIconCatalog
{
    public const string Edit = "\uE70F";
    public const string Settings = "\uE713";
    public const string Filter = "\uE71C";
    public const string Search = "\uE721";
    public const string Delete = "\uE74D";
    public const string CommandPrompt = "\uE756";
    public const string EraseTool = "\uE75C";
    public const string Paste = "\uE77F";
    public const string OpenWith = "\uE7AC";
    public const string Package = "\uE7B8";
    public const string Admin = "\uE7EF";
    public const string ClosePane = "\uE89F";
    public const string OpenPane = "\uE8A0";
    public const string Document = "\uE8A5";
    public const string ViewAll = "\uE8A9";
    public const string Switch = "\uE8AB";
    public const string Rename = "\uE8AC";
    public const string SelectAll = "\uE8B3";
    public const string Import = "\uE8B5";
    public const string Folder = "\uE8B7";
    public const string Cut = "\uE8C6";
    public const string Copy = "\uE8C8";
    public const string MoveToFolder = "\uE8DE";
    public const string OpenFile = "\uE8E5";
    public const string NewFolder = "\uE8F4";
    public const string BulletedList = "\uE8FD";
    public const string Preview = "\uE8FF";
    public const string Label = "\uE932";
    public const string Favorite = "\uE734";
    public const string NewTab = "\uE8BB";
    public const string Info = "\uE946";
    public const string AreaChart = "\uE9D2";
    public const string List = "\uEA37";
    public const string Tiles = "\uECA5";

    public static string? GlyphFor(string commandId)
    {
        return commandId switch
        {
            "ctx-open" => OpenFile,
            "ctx-open-tab" => NewTab,
            "ctx-open-other-pane" => OpenPane,
            "ctx-open-with" or "ctx-open-with-choose" => OpenWith,
            "ctx-preview" => Preview,
            "ctx-compare" => Switch,
            "ctx-terminal" => CommandPrompt,
            "ctx-powershell-admin" => Admin,
            "ctx-color-label" => Label,
            "ctx-folder-metrics" => AreaChart,
            "ctx-cleanup" => EraseTool,
            "ctx-duplicates" => SelectAll,
            "ctx-rename" => Rename,
            "ctx-advanced-rename" => Edit,
            "ctx-copy" or "ctx-copy-to-pane" or "ctx-copy-path" => Copy,
            "ctx-bookmark" => Favorite,
            "ctx-cut" => Cut,
            "ctx-paste" => Paste,
            "ctx-move-to-pane" => MoveToFolder,
            "ctx-pack" => Folder,
            "ctx-unpack" => Import,
            "ctx-compress" or "ctx-view-archive" => Package,
            "ctx-extract-menu" or "ctx-extract-to" => Import,
            "ctx-delete-menu" => Delete,
            "ctx-info" => Info,
            "ctx-close-left-pane" or "ctx-close-dual-pane" => ClosePane,
            "overflow-search" => Search,
            "overflow-filter" => Filter,
            "overflow-new-folder" => NewFolder,
            "overflow-new-file" => Document,
            "overflow-dual-pane" => OpenPane,
            "overflow-view" => ViewAll,
            "overflow-settings" => Settings,
            "view:details" => BulletedList,
            "view:list" => List,
            "view:tiles" => Tiles,
            "view:content" => Document,
            _ => null,
        };
    }
}
