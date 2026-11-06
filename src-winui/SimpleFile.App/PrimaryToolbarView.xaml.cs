using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using SimpleFile.Core;

namespace SimpleFile.App;

public sealed partial class PrimaryToolbarView : UserControl
{
    public PrimaryToolbarView()
    {
        InitializeComponent();
    }

    public Grid ToolbarRoot => PrimaryToolbar;
    public ColumnDefinition ActionsColumn => PrimaryActionsColumn;
    public StackPanel NavHost => PrimaryNavHost;
    public Button SidebarToggleButton => PrimarySidebarToggleButton;
    public Button BackButton => PrimaryBackButton;
    public Button ForwardButton => PrimaryForwardButton;
    public Button UpButton => PrimaryUpButton;
    public Grid OmnibarRoot => OmnibarHost;
    public TextBox OmnibarTextBox => OmnibarBox;
    public ToggleButton OmnibarPathMode => OmnibarPathModeButton;
    public ToggleButton OmnibarSearchMode => OmnibarSearchModeButton;
    public ToggleButton OmnibarFilterMode => OmnibarFilterModeButton;
    public ToggleButton OmnibarCommandMode => OmnibarCommandModeButton;
    public Button OmnibarExecute => OmnibarExecuteButton;
    public ToggleButton ContentSearchToggle => ContentSearchButton;
    public Button SearchCancel => SearchCancelButton;
    public StackPanel ActionsHost => PrimaryActionsHost;
    public Button NewButton => PrimaryNewButton;
    public Button DualPaneToggleButton => DualPaneButton;
    public Button ClosePaneButton => ClosePrimaryPaneButton;
    public Button ProfileButton => WorkspaceProfileButton;
    public StackPanel WorkspaceProfilesList => WorkspaceProfilesHost;
    public Button WorkspaceProfileSave => WorkspaceProfileSaveButton;
    public Button WorkspaceProfileManage => WorkspaceProfileManageButton;
    public Button ViewButton => PrimaryViewButton;
    public RadioButtons ViewStyleOptions => ViewStyleRadioButtons;
    public TextBlock ViewIconSizeValue => ViewIconSizeValueText;
    public Slider ViewIconSize => ViewIconSizeSlider;
    public Button ViewApplyBoth => ViewApplyBothButton;
    public TextBlock ViewFolderRuleStatus => ViewFolderRuleStatusText;
    public ComboBox ViewRememberScope => ViewRememberScopeBox;
    public ComboBoxItem ViewRememberFolder => ViewRememberFolderItem;
    public ComboBoxItem ViewRememberDescendants => ViewRememberDescendantsItem;
    public Button ViewRemember => ViewRememberButton;
    public TextBlock ViewIconSizeMin => ViewIconSizeMinLabel;
    public TextBlock ViewIconSizeMax => ViewIconSizeMaxLabel;
    public Button SettingsButton => PrimarySettingsButton;
    public MenuFlyoutItem NewFolderItem => NewFolderMenuItem;
    public MenuFlyoutItem NewTextItem => NewTextMenuItem;
    public Button MoreButton => PrimaryMoreButton;

    public event SizeChangedEventHandler? PrimaryToolbarSizeChanged;
    public event RoutedEventHandler? ToggleSidebar;
    public event RoutedEventHandler? PrimaryBack;
    public event RoutedEventHandler? PrimaryForward;
    public event RoutedEventHandler? PrimaryUp;
    public event KeyEventHandler? OmnibarKeyDown;
    public event RoutedEventHandler? OmnibarExecuteClick;
    public event EventHandler<OmnibarMode>? OmnibarModeRequested;
    public event RoutedEventHandler? OmnibarGotFocus;
    public event RoutedEventHandler? OmnibarLostFocus;
    public event TextChangedEventHandler? OmnibarTextChanged;
    public event RoutedEventHandler? ContentSearchToggleClick;
    public event RoutedEventHandler? CancelSearchClick;
    public event EventHandler<string>? PrimaryNewItemRequested;
    public event RoutedEventHandler? ToggleDualPane;
    public event RoutedEventHandler? ClosePrimaryPane;
    public event EventHandler<object>? WorkspaceProfilesFlyoutOpening;
    public event RoutedEventHandler? WorkspaceProfileSaveClicked;
    public event RoutedEventHandler? WorkspaceProfileManageClicked;
    public event EventHandler<object>? ViewOptionsFlyoutOpening;
    public event SelectionChangedEventHandler? ViewStyleSelectionChanged;
    public event RangeBaseValueChangedEventHandler? ViewIconSizeSliderChanged;
    public event RoutedEventHandler? ViewApplyBothClicked;
    public event EventHandler<FolderViewScope>? ViewRememberRequested;
    public event RoutedEventHandler? SettingsClicked;
    public event EventHandler<object>? PrimaryMoreMenuOpening;

    private void OnPrimaryToolbarSizeChanged(object sender, SizeChangedEventArgs e) =>
        PrimaryToolbarSizeChanged?.Invoke(sender, e);

    private void OnToggleSidebar(object sender, RoutedEventArgs e) => ToggleSidebar?.Invoke(sender, e);

    private void OnPrimaryBack(object sender, RoutedEventArgs e) => PrimaryBack?.Invoke(sender, e);

    private void OnPrimaryForward(object sender, RoutedEventArgs e) => PrimaryForward?.Invoke(sender, e);

    private void OnPrimaryUp(object sender, RoutedEventArgs e) => PrimaryUp?.Invoke(sender, e);

    private void OnOmnibarKeyDown(object sender, KeyRoutedEventArgs e) => OmnibarKeyDown?.Invoke(sender, e);

    private void OnOmnibarExecuteClick(object sender, RoutedEventArgs e) => OmnibarExecuteClick?.Invoke(sender, e);

    private void OnOmnibarPathModeClick(object sender, RoutedEventArgs e) => RequestOmnibarMode(OmnibarMode.Navigate);

    private void OnOmnibarSearchModeClick(object sender, RoutedEventArgs e) => RequestOmnibarMode(OmnibarMode.Search);

    private void OnOmnibarFilterModeClick(object sender, RoutedEventArgs e) => RequestOmnibarMode(OmnibarMode.Filter);

    private void OnOmnibarCommandModeClick(object sender, RoutedEventArgs e) => RequestOmnibarMode(OmnibarMode.Command);

    /// <summary>Mode toggles are focusable; after a click focus returns to the text box so typing continues.</summary>
    private void RequestOmnibarMode(OmnibarMode mode)
    {
        OmnibarModeRequested?.Invoke(this, mode);
        OmnibarBox.Focus(FocusState.Programmatic);
    }

    private void OnOmnibarGotFocus(object sender, RoutedEventArgs e) => OmnibarGotFocus?.Invoke(sender, e);

    private void OnOmnibarLostFocus(object sender, RoutedEventArgs e) => OmnibarLostFocus?.Invoke(sender, e);

    private void OnOmnibarTextChanged(object sender, TextChangedEventArgs e) => OmnibarTextChanged?.Invoke(sender, e);

    private void OnContentSearchToggle(object sender, RoutedEventArgs e) => ContentSearchToggleClick?.Invoke(sender, e);

    private void OnCancelSearchClick(object sender, RoutedEventArgs e) => CancelSearchClick?.Invoke(sender, e);


    private void OnNewMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string id })
        {
            PrimaryNewItemRequested?.Invoke(this, id);
        }
    }

    private void OnToggleDualPane(object sender, RoutedEventArgs e) => ToggleDualPane?.Invoke(sender, e);

    private void OnClosePrimaryPane(object sender, RoutedEventArgs e) => ClosePrimaryPane?.Invoke(sender, e);

    private void OnWorkspaceProfilesFlyoutOpening(object sender, object e) =>
        WorkspaceProfilesFlyoutOpening?.Invoke(sender, e);

    private void OnWorkspaceProfileSaveClicked(object sender, RoutedEventArgs e) =>
        WorkspaceProfileSaveClicked?.Invoke(sender, e);

    private void OnWorkspaceProfileManageClicked(object sender, RoutedEventArgs e) =>
        WorkspaceProfileManageClicked?.Invoke(sender, e);

    private void OnViewOptionsFlyoutOpening(object sender, object e) => ViewOptionsFlyoutOpening?.Invoke(sender, e);

    private void OnViewStyleSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewStyleSelectionChanged?.Invoke(sender, e);

    private void OnViewIconSizeSliderChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        ViewIconSizeSliderChanged?.Invoke(sender, e);

    private void OnViewApplyBothClicked(object sender, RoutedEventArgs e) => ViewApplyBothClicked?.Invoke(sender, e);

    /// <summary>
    /// "Remember this view for" + Remember: raises one request carrying the chosen scope
    /// (everywhere, this folder, or this folder and subfolders) for the window to save.
    /// </summary>
    private void OnViewRememberClicked(object sender, RoutedEventArgs e)
    {
        var scope = ((ViewRememberScopeBox.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "folder" => FolderViewScope.Folder,
            "descendants" => FolderViewScope.Descendants,
            _ => FolderViewScope.Global,
        };
        ViewRememberRequested?.Invoke(this, scope);
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => SettingsClicked?.Invoke(sender, e);

    private void OnPrimaryMoreMenuOpening(object sender, object e) => PrimaryMoreMenuOpening?.Invoke(sender, e);
}
