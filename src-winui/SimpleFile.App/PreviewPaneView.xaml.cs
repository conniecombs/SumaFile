using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SimpleFile.App;

public sealed partial class PreviewPaneView : UserControl
{
    public PreviewPaneView()
    {
        InitializeComponent();
    }

    public Border PaneRoot => PreviewPane;
    public TextBlock TitleText => PreviewTitle;
    public TextBlock SubtitleText => PreviewSubtitle;
    public Button OpenButton => PreviewOpenButton;
    public Button OpenWithButton => PreviewOpenWithButton;
    public Button RevealButton => PreviewRevealButton;
    public Button CompareButton => PreviewCompareButton;
    public Button ChecksumButton => PreviewChecksumButton;
    public StackPanel IconPanel => PreviewIconPanel;
    public Image IconImage => PreviewIconImage;
    public TextBlock IconLabel => PreviewIconLabel;
    public Image ImagePreview => PreviewImage;
    public TextBox TextPreview => PreviewTextBox;
    public TextBlock EmptyText => PreviewEmptyText;
    public StackPanel MetadataRows => PreviewMetadataRows;
    public TextBlock ChecksumText => PreviewChecksumText;

    public event RoutedEventHandler? TogglePreview;
    public event RoutedEventHandler? PreviewOpenClick;
    public event RoutedEventHandler? PreviewOpenWithClick;
    public event RoutedEventHandler? PreviewRevealClick;
    public event RoutedEventHandler? PreviewCompareClick;
    public event RoutedEventHandler? PreviewChecksumClick;

    private void OnTogglePreview(object sender, RoutedEventArgs e) => TogglePreview?.Invoke(sender, e);

    private void OnPreviewOpenClick(object sender, RoutedEventArgs e) => PreviewOpenClick?.Invoke(sender, e);

    private void OnPreviewOpenWithClick(object sender, RoutedEventArgs e) => PreviewOpenWithClick?.Invoke(sender, e);

    private void OnPreviewRevealClick(object sender, RoutedEventArgs e) => PreviewRevealClick?.Invoke(sender, e);

    private void OnPreviewCompareClick(object sender, RoutedEventArgs e) => PreviewCompareClick?.Invoke(sender, e);

    private void OnPreviewChecksumClick(object sender, RoutedEventArgs e) => PreviewChecksumClick?.Invoke(sender, e);
}
