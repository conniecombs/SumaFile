using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace SimpleFile.App;

public sealed partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
    }

    public Grid Root => SidebarRoot;
    public Border TargetSwitch => SidebarTargetSwitch;
    public Button LeftButton => SidebarLeftButton;
    public Button RightButton => SidebarRightButton;
    public Button CloseButton => SidebarCloseButton;
    public StackPanel QuickAccessSectionRoot => QuickAccessSection;
    public Button QuickAccessCollapse => QuickAccessCollapseButton;
    public ListView QuickAccess => QuickAccessList;
    public Button RefreshDrives => RefreshDrivesButton;
    public Button MyPcCollapse => MyPcCollapseButton;
    public ListView DriveItems => DriveList;
    public StackPanel FolderTreeSectionRoot => FolderTreeSection;
    public TextBlock FolderTreeEmpty => FolderTreeEmptyText;
    public ListView FolderTree => FolderTreeList;
    public StackPanel BookmarksSectionRoot => BookmarksSection;
    public TextBlock BookmarksEmpty => BookmarksEmptyText;
    public ListView Bookmarks => BookmarksList;
    public StackPanel RecentSectionRoot => RecentSection;
    public Button ClearRecents => ClearRecentsButton;
    public TextBlock RecentsEmpty => RecentsEmptyText;
    public ListView Recents => RecentsList;
    public StackPanel SmartFoldersSectionRoot => SmartFoldersSection;
    public TextBlock SmartFoldersEmpty => SmartFoldersEmptyText;
    public ListView SmartFolders => SmartFoldersList;

    public event RoutedEventHandler? SidebarLeft;
    public event RoutedEventHandler? SidebarRight;
    public event RoutedEventHandler? ToggleSidebar;
    public event RoutedEventHandler? ToggleQuickAccess;
    public event ItemClickEventHandler? QuickAccessClick;
    public event RoutedEventHandler? RefreshDrivesClick;
    public event RoutedEventHandler? ToggleMyPc;
    public event ItemClickEventHandler? DriveClick;
    public event RoutedEventHandler? RefreshFolderTree;
    public event ItemClickEventHandler? FolderTreeClick;
    public event RoutedEventHandler? FolderTreeToggle;
    public event RoutedEventHandler? AddBookmark;
    public event ItemClickEventHandler? BookmarkClick;
    public event RoutedEventHandler? RemoveBookmark;
    public event RoutedEventHandler? ClearRecentHistory;
    public event ItemClickEventHandler? RecentClick;
    public event RoutedEventHandler? SaveSmartFolder;
    public event ItemClickEventHandler? SmartFolderClicked;
    public event RoutedEventHandler? DeleteSmartFolderClicked;

    private void OnSidebarLeft(object sender, RoutedEventArgs e) => SidebarLeft?.Invoke(sender, e);

    private void OnSidebarRight(object sender, RoutedEventArgs e) => SidebarRight?.Invoke(sender, e);

    private void OnToggleSidebar(object sender, RoutedEventArgs e) => ToggleSidebar?.Invoke(sender, e);

    private void OnToggleQuickAccess(object sender, RoutedEventArgs e) => ToggleQuickAccess?.Invoke(sender, e);

    private void OnQuickAccessClick(object sender, ItemClickEventArgs e) => QuickAccessClick?.Invoke(sender, e);

    private void OnRefreshDrives(object sender, RoutedEventArgs e) => RefreshDrivesClick?.Invoke(sender, e);

    private void OnToggleMyPc(object sender, RoutedEventArgs e) => ToggleMyPc?.Invoke(sender, e);

    private void OnDriveClick(object sender, ItemClickEventArgs e) => DriveClick?.Invoke(sender, e);

    private void OnRefreshFolderTree(object sender, RoutedEventArgs e) => RefreshFolderTree?.Invoke(sender, e);

    private void OnFolderTreeClick(object sender, ItemClickEventArgs e) => FolderTreeClick?.Invoke(sender, e);

    private void OnFolderTreeToggle(object sender, RoutedEventArgs e) => FolderTreeToggle?.Invoke(sender, e);

    private void OnAddBookmark(object sender, RoutedEventArgs e) => AddBookmark?.Invoke(sender, e);

    private void OnBookmarkClick(object sender, ItemClickEventArgs e) => BookmarkClick?.Invoke(sender, e);

    private void OnRemoveBookmark(object sender, RoutedEventArgs e) => RemoveBookmark?.Invoke(sender, e);

    private void OnClearRecentHistory(object sender, RoutedEventArgs e) => ClearRecentHistory?.Invoke(sender, e);

    private void OnRecentClick(object sender, ItemClickEventArgs e) => RecentClick?.Invoke(sender, e);

    private void OnSaveSmartFolder(object sender, RoutedEventArgs e) => SaveSmartFolder?.Invoke(sender, e);

    private void OnSmartFolderClicked(object sender, ItemClickEventArgs e) => SmartFolderClicked?.Invoke(sender, e);

    private void OnDeleteSmartFolderClicked(object sender, RoutedEventArgs e) =>
        DeleteSmartFolderClicked?.Invoke(sender, e);
}
