using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SimpleFile.App;

public sealed partial class PreviewPaneView : UserControl
{
    private const double LabeledActionWidth = 360;

    public PreviewPaneView()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateActionLayout();
        SizeChanged += (_, _) => UpdateActionLayout();
    }

    public Border PaneRoot => PreviewPane;
    public TextBlock TitleText => PreviewTitle;
    public TextBlock SubtitleText => PreviewSubtitle;
    public Button OpenButton => PreviewOpenButton;
    public Button OpenWithButton => PreviewOpenWithButton;
    public Button RevealButton => PreviewRevealButton;
    public Button CompareButton => PreviewCompareButton;
    public Button ChecksumButton => PreviewChecksumButton;
    public Button MoreActionsButton => PreviewMoreActionsButton;
    public MenuFlyoutItem CompareMenuItem => PreviewCompareMenuItem;
    public MenuFlyoutItem ChecksumMenuItem => PreviewChecksumMenuItem;
    public StackPanel OptionsPanel => PreviewOptionsPanel;
    public CheckBox RenderHtmlCheckBox => PreviewRenderHtmlCheckBox;
    public CheckBox VideoPlaybackCheckBox => PreviewVideoPlaybackCheckBox;
    public StackPanel IconPanel => PreviewIconPanel;
    public Image IconImage => PreviewIconImage;
    public TextBlock IconLabel => PreviewIconLabel;
    public Image ImagePreview => PreviewImage;
    public WebView2 PdfPreview => PreviewPdfWebView;
    public MediaPlayerElement MediaPreview => PreviewMediaPlayer;
    public StackPanel VideoFrameControls => PreviewVideoFrameControls;
    public Image VideoFrameImage => PreviewVideoFrameImage;
    public RadioButtons VideoFramePresets => PreviewVideoFramePresetOptions;
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

    private void UpdateActionLayout()
    {
        var showLabels = ActualWidth >= LabeledActionWidth;
        ConfigureActionButton(PreviewOpenButton, "\uE8E5", "Open", showLabels);
        ConfigureActionButton(PreviewOpenWithButton, "\uE7AC", "Open with", showLabels);
        ConfigureActionButton(PreviewRevealButton, "\uE8DA", "Reveal", showLabels);
        ConfigureActionButton(PreviewCompareButton, "\uE8AB", "Compare", showLabels);
        ConfigureActionButton(PreviewChecksumButton, "\uE9D9", "Checksums", showLabels);
        PreviewCompareButton.Visibility = showLabels ? Visibility.Visible : Visibility.Collapsed;
        PreviewChecksumButton.Visibility = showLabels ? Visibility.Visible : Visibility.Collapsed;
        PreviewMoreActionsButton.Visibility = showLabels ? Visibility.Collapsed : Visibility.Visible;
        PreviewMoreActionsButton.Content = Icon("\uE712");
    }

    private static void ConfigureActionButton(Button button, string glyph, string label, bool showLabel)
    {
        button.Width = showLabel ? double.NaN : 30;
        button.MinWidth = showLabel ? 74 : 30;
        button.Padding = showLabel ? new Thickness(9, 0, 10, 0) : new Thickness(0);
        button.Content = showLabel
            ? new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    Icon(glyph),
                    new TextBlock
                    {
                        Text = label,
                        FontSize = 12,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 82,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            }
            : Icon(glyph);
    }

    private static FontIcon Icon(string glyph)
    {
        return new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 14,
            Glyph = glyph,
        };
    }
}
