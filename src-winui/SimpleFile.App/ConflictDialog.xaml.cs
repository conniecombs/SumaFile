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
        ChoiceButtons.SelectedIndex = SkipIndex;
        Result = ConflictResolution.Cancel;
    }

    public void SetComparisons(IReadOnlyList<TransferConflictComparison> comparisons)
    {
        ComparisonCards.Children.Clear();
        foreach (var comparison in comparisons)
        {
            var columns = new Grid { ColumnSpacing = 16 };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.Children.Add(FileCard(
                "Incoming item",
                comparison.Incoming,
                TransferConflictPromptFormatter.Badges(comparison.Incoming, comparison.Existing)));
            var existing = FileCard(
                "Existing item",
                comparison.Existing,
                TransferConflictPromptFormatter.Badges(comparison.Existing, comparison.Incoming));
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

    private static StackPanel FileCard(string title, ConflictFileSnapshot file, IReadOnlyList<string> badges)
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
