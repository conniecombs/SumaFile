using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace SimpleFile.App;

public sealed partial class PrimaryToolbarView : UserControl
{
    public PrimaryToolbarView()
    {
        InitializeComponent();
    }

    public Grid ToolbarRoot => PrimaryToolbar;
    public ColumnDefinition SearchColumn => PrimarySearchColumn;
    public ColumnDefinition ActionsColumn => PrimaryActionsColumn;
    public StackPanel NavHost => PrimaryNavHost;
    public Button SidebarToggleButton => PrimarySidebarToggleButton;
    public Button BackButton => PrimaryBackButton;
    public Button ForwardButton => PrimaryForwardButton;
    public Button UpButton => PrimaryUpButton;
    public Grid SearchHost => PrimarySearchHost;
    public TextBox SearchTextBox => SearchBox;
    public ToggleButton ContentSearchToggle => ContentSearchButton;
    public Button SearchCancel => SearchCancelButton;
    public StackPanel ActionsHost => PrimaryActionsHost;
    public TextBox QuickFilterTextBox => QuickFilterBox;
    public Button NewFolderButton => PrimaryNewFolderButton;
    public Button NewFileButton => PrimaryNewFileButton;
    public Button DualPaneToggleButton => DualPaneButton;
    public Button ClosePaneButton => ClosePrimaryPaneButton;
    public Button ProfileButton => WorkspaceProfileButton;
    public StackPanel WorkspaceProfilesList => WorkspaceProfilesHost;
    public Button WorkspaceProfileSave => WorkspaceProfileSaveButton;
    public Button WorkspaceProfileManage => WorkspaceProfileManageButton;
    public Button ViewButton => PrimaryViewButton;
    public Button ViewDualPaneToggleButton => ViewDualPaneButton;
    public FontIcon ViewDualPaneGlyph => ViewDualPaneIcon;
    public TextBlock ViewDualPaneLabel => ViewDualPaneText;
    public RadioButtons ViewStyleOptions => ViewStyleRadioButtons;
    public TextBlock ViewIconSizeValue => ViewIconSizeValueText;
    public Slider ViewIconSize => ViewIconSizeSlider;
    public Button ViewApplyBoth => ViewApplyBothButton;
    public Button ViewUseGlobally => ViewUseGloballyButton;
    public Button ViewUseForFolder => ViewUseForFolderButton;
    public Button ViewUseForDescendants => ViewUseForDescendantsButton;
    public TextBlock ViewFolderRuleStatus => ViewFolderRuleStatusText;
    public Button ViewSaveProfile => ViewSaveProfileButton;
    public StackPanel ViewProfilesList => ViewProfilesHost;
    public Button SettingsButton => PrimarySettingsButton;
    public Button MoreButton => PrimaryMoreButton;

    public event SizeChangedEventHandler? PrimaryToolbarSizeChanged;
    public event RoutedEventHandler? ToggleSidebar;
    public event RoutedEventHandler? PrimaryBack;
    public event RoutedEventHandler? PrimaryForward;
    public event RoutedEventHandler? PrimaryUp;
    public event KeyEventHandler? SearchKeyDown;
    public event RoutedEventHandler? SearchClick;
    public event RoutedEventHandler? ContentSearchToggleClick;
    public event RoutedEventHandler? CancelSearchClick;
    public event TextChangedEventHandler? QuickFilterChanged;
    public event RoutedEventHandler? PrimaryNewFolder;
    public event RoutedEventHandler? PrimaryNewFile;
    public event RoutedEventHandler? ToggleDualPane;
    public event RoutedEventHandler? ClosePrimaryPane;
    public event EventHandler<object>? WorkspaceProfilesFlyoutOpening;
    public event RoutedEventHandler? WorkspaceProfileSaveClicked;
    public event RoutedEventHandler? WorkspaceProfileManageClicked;
    public event EventHandler<object>? ViewOptionsFlyoutOpening;
    public event RoutedEventHandler? ViewDualPaneClicked;
    public event SelectionChangedEventHandler? ViewStyleSelectionChanged;
    public event RangeBaseValueChangedEventHandler? ViewIconSizeSliderChanged;
    public event RoutedEventHandler? ViewApplyBothClicked;
    public event RoutedEventHandler? ViewUseGloballyClicked;
    public event RoutedEventHandler? ViewUseForFolderClicked;
    public event RoutedEventHandler? ViewUseForDescendantsClicked;
    public event RoutedEventHandler? ViewSaveProfileClicked;
    public event RoutedEventHandler? SettingsClicked;
    public event EventHandler<object>? PrimaryMoreMenuOpening;

    private void OnPrimaryToolbarSizeChanged(object sender, SizeChangedEventArgs e) =>
        PrimaryToolbarSizeChanged?.Invoke(sender, e);

    private void OnToggleSidebar(object sender, RoutedEventArgs e) => ToggleSidebar?.Invoke(sender, e);

    private void OnPrimaryBack(object sender, RoutedEventArgs e) => PrimaryBack?.Invoke(sender, e);

    private void OnPrimaryForward(object sender, RoutedEventArgs e) => PrimaryForward?.Invoke(sender, e);

    private void OnPrimaryUp(object sender, RoutedEventArgs e) => PrimaryUp?.Invoke(sender, e);

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e) => SearchKeyDown?.Invoke(sender, e);

    private void OnSearchClick(object sender, RoutedEventArgs e) => SearchClick?.Invoke(sender, e);

    private void OnContentSearchToggle(object sender, RoutedEventArgs e) => ContentSearchToggleClick?.Invoke(sender, e);

    private void OnCancelSearchClick(object sender, RoutedEventArgs e) => CancelSearchClick?.Invoke(sender, e);

    private void OnQuickFilterChanged(object sender, TextChangedEventArgs e) => QuickFilterChanged?.Invoke(sender, e);

    private void OnPrimaryNewFolder(object sender, RoutedEventArgs e) => PrimaryNewFolder?.Invoke(sender, e);

    private void OnPrimaryNewFile(object sender, RoutedEventArgs e) => PrimaryNewFile?.Invoke(sender, e);

    private void OnToggleDualPane(object sender, RoutedEventArgs e) => ToggleDualPane?.Invoke(sender, e);

    private void OnClosePrimaryPane(object sender, RoutedEventArgs e) => ClosePrimaryPane?.Invoke(sender, e);

    private void OnWorkspaceProfilesFlyoutOpening(object sender, object e) =>
        WorkspaceProfilesFlyoutOpening?.Invoke(sender, e);

    private void OnWorkspaceProfileSaveClicked(object sender, RoutedEventArgs e) =>
        WorkspaceProfileSaveClicked?.Invoke(sender, e);

    private void OnWorkspaceProfileManageClicked(object sender, RoutedEventArgs e) =>
        WorkspaceProfileManageClicked?.Invoke(sender, e);

    private void OnViewOptionsFlyoutOpening(object sender, object e) => ViewOptionsFlyoutOpening?.Invoke(sender, e);

    private void OnViewDualPaneClicked(object sender, RoutedEventArgs e) => ViewDualPaneClicked?.Invoke(sender, e);

    private void OnViewStyleSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewStyleSelectionChanged?.Invoke(sender, e);

    private void OnViewIconSizeSliderChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        ViewIconSizeSliderChanged?.Invoke(sender, e);

    private void OnViewApplyBothClicked(object sender, RoutedEventArgs e) => ViewApplyBothClicked?.Invoke(sender, e);

    private void OnViewUseGloballyClicked(object sender, RoutedEventArgs e) => ViewUseGloballyClicked?.Invoke(sender, e);

    private void OnViewUseForFolderClicked(object sender, RoutedEventArgs e) => ViewUseForFolderClicked?.Invoke(sender, e);

    private void OnViewUseForDescendantsClicked(object sender, RoutedEventArgs e) => ViewUseForDescendantsClicked?.Invoke(sender, e);

    private void OnViewSaveProfileClicked(object sender, RoutedEventArgs e) => ViewSaveProfileClicked?.Invoke(sender, e);

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => SettingsClicked?.Invoke(sender, e);

    private void OnPrimaryMoreMenuOpening(object sender, object e) => PrimaryMoreMenuOpening?.Invoke(sender, e);
}
