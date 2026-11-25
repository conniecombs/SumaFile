using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;

namespace SimpleFile.App;

public enum ConflictResolution
{
    Cancel,
    Skip,
    Replace,
    KeepBoth,
}

public sealed partial class ConflictDialog : ContentDialog
{
    private const int ReplaceIndex = 0;
    private const int SkipIndex = 1;
    private const int KeepBothIndex = 2;

    public ConflictResolution Result { get; private set; } = ConflictResolution.Cancel;
    public bool ApplyToAllChecked => ApplyToAll.IsChecked == true;

    /// <summary>
    /// Reads a small picture of a file for its card; null when it has none. The window sets it, as it has the file
    /// service. Without it the cards are text only.
    /// </summary>
    public Func<string, CancellationToken, Task<ImageSource?>>? ThumbnailLoader { get; set; }

    /// <summary>How many cards show a picture so far; the pictures arrive after the cards.</summary>
    internal int ShownThumbnails { get; private set; }

    // Ends the picture reads of the comparisons shown before, when new ones are set or the dialog closes.
    private CancellationTokenSource? _thumbnails;

    public ConflictDialog()
    {
        InitializeComponent();
        // Enter = Continue with the selected choice; Close / Escape stops the transfer.
        PrimaryButtonClick += (_, _) => Result = ChoiceButtons.SelectedIndex switch
        {
            ReplaceIndex => ConflictResolution.Replace,
            KeepBothIndex => ConflictResolution.KeepBoth,
            _ => ConflictResolution.Skip,
        };
        CloseButtonClick += (_, _) => Result = ConflictResolution.Cancel;
        Closed += (_, _) => _thumbnails?.Cancel();
    }

    public void SetConflict(string destination, IReadOnlyList<string> conflicts)
    {
        SetConflict(new TransferConflictPromptContext(
            Move: false,
            SourceCount: Math.Max(conflicts.Count, 1),
            Destination: destination,
            TargetPane: null,
            Conflicts: conflicts));
    }

    /// <summary>
    /// Fills the dialog for one prompt. The same instance is reused for each conflict in a
    /// batch, so the choice resets to Skip and the result to Cancel every time.
    /// </summary>
    public void SetConflict(TransferConflictPromptContext context)
    {
        var text = TransferConflictPromptFormatter.Format(context);
        Title = text.Title;
        OperationSummary.Text = text.OperationSummary;
        DestinationSummary.Text = text.DestinationSummary;
        ConflictMessage.Text = text.ConflictSummary;
        ChoiceHint.Text = "";
        ChoiceHint.Visibility = Visibility.Collapsed;
        KeepBothOption.Text = TransferConflictPromptFormatter.KeepBothLabel(null);
        ComparisonCards.Children.Clear();
        ReplaceChoice.IsEnabled = true;
        ChoiceButtons.SelectedIndex = SkipIndex;
        Result = ConflictResolution.Cancel;
    }

    public void SetComparisons(IReadOnlyList<TransferConflictComparison> comparisons, bool allowReplace = true)
    {
        ReplaceChoice.IsEnabled = allowReplace && !comparisons.Any(c =>
            (c.Incoming.Kind == "Folder" && c.Existing.Kind is "File" or "Link" or "Folder")
            || (c.Existing.Kind == "Folder" && c.Incoming.Kind is "File" or "Link")
            || PathRules.PathsEqual(c.Incoming.Path, c.Existing.Path));
        ComparisonCards.Children.Clear();
        _thumbnails?.Cancel();
        _thumbnails = new CancellationTokenSource();
        ShownThumbnails = 0;
        var token = _thumbnails.Token;
        foreach (var comparison in comparisons)
        {
            var columns = new Grid { ColumnSpacing = 16 };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.Children.Add(FileCard(
                "Incoming item",
                comparison.Incoming,
                TransferConflictPromptFormatter.Badges(comparison.Incoming, comparison.Existing),
                token));
            var existing = FileCard(
                "Existing item",
                comparison.Existing,
                TransferConflictPromptFormatter.Badges(comparison.Existing, comparison.Incoming),
                token);
            Grid.SetColumn(existing, 1);
            columns.Children.Add(existing);
            var card = new StackPanel { Spacing = 6 };
            card.Children.Add(columns);
            card.Children.Add(new TextBlock { Text = comparison.Comparison, TextWrapping = TextWrapping.Wrap });
            ComparisonCards.Children.Add(card);
        }

        var keepBothName = comparisons.Count == 1 ? comparisons[0].KeepBothName : null;
        KeepBothOption.Text = TransferConflictPromptFormatter.KeepBothLabel(keepBothName);
        if (!string.IsNullOrWhiteSpace(keepBothName))
        {
            ChoiceHint.Text = "The kept name is a preview; another file created during the transfer may change it.";
            ChoiceHint.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// One side of a comparison: its details, and for a picture a thumbnail to their left. The thumbnail is read
    /// after the card is shown and takes its place only when it arrives, so a card never waits for it.
    /// </summary>
    private FrameworkElement FileCard(string title, ConflictFileSnapshot file, IReadOnlyList<string> badges, CancellationToken token)
    {
        var details = FileDetails(title, file, badges);
        if (ThumbnailLoader is not { } load || !file.HasThumbnail)
        {
            return details;
        }

        var card = new Grid { ColumnSpacing = 10 };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var picture = new Image { Width = ThumbnailSize, Height = ThumbnailSize, Stretch = Stretch.Uniform };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picture, $"{title} picture");
        var frame = new Border
        {
            Width = ThumbnailSize,
            Height = ThumbnailSize,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Child = picture,
        };
        card.Children.Add(frame);
        Grid.SetColumn(details, 1);
        card.Children.Add(details);
        _ = ShowThumbnailAsync(frame, picture, file.Path, load, token);
        return card;
    }

    private const double ThumbnailSize = 64;

    private async Task ShowThumbnailAsync(
        Border frame,
        Image picture,
        string path,
        Func<string, CancellationToken, Task<ImageSource?>> load,
        CancellationToken token)
    {
        try
        {
            var source = await load(path, token);
            if (source is null || token.IsCancellationRequested)
            {
                return;
            }

            picture.Source = source;
            frame.Visibility = Visibility.Visible;
            ShownThumbnails++;
        }
        catch (Exception)
        {
            // A card without its picture is still a whole card: the details are what the choice is made on.
        }
    }

    private static StackPanel FileDetails(string title, ConflictFileSnapshot file, IReadOnlyList<string> badges)
    {
        var card = new StackPanel { Spacing = 4 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        foreach (var badge in badges)
        {
            header.Children.Add(Badge(badge));
        }

        card.Children.Add(header);
        card.Children.Add(new TextBlock { Text = file.Path, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        card.Children.Add(new TextBlock { Text = file.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        return card;
    }

    private static Border Badge(string text)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 0, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 11 },
        };
        border.BorderBrush = Application.Current.Resources.TryGetValue("SfBorderBrush", out var brush) && brush is Brush borderBrush
            ? borderBrush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);

        return border;
    }
}
