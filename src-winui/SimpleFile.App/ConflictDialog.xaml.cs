using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    public ConflictResolution Result { get; private set; } = ConflictResolution.Cancel;
    public bool ApplyToAllChecked => ApplyToAll.IsChecked == true;

    public ConflictDialog()
    {
        InitializeComponent();
        PrimaryButtonClick += (_, _) => Result = ConflictResolution.Replace;
        SecondaryButtonClick += (_, _) => Result = ConflictResolution.Skip;
        CloseButtonClick += (_, _) => Result = ConflictResolution.Cancel;
        KeepBothButton.Click += (_, _) =>
        {
            Result = ConflictResolution.KeepBoth;
            Hide();
        };
    }

    public void SetConflictPath(string path)
    {
        var destination = PathRules.GetParentPath(path) ?? "";
        var name = PathRules.Basename(path);
        SetConflict(new TransferConflictPromptContext(
            Move: false,
            SourceCount: 1,
            Destination: destination,
            TargetPane: null,
            Conflicts: string.IsNullOrWhiteSpace(name) ? [] : [name]));
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

    public void SetConflict(TransferConflictPromptContext context)
    {
        var text = TransferConflictPromptFormatter.Format(context);
        Title = text.Title;
        OperationSummary.Text = text.OperationSummary;
        DestinationSummary.Text = text.DestinationSummary;
        ConflictMessage.Text = text.ConflictSummary;
        ChoiceHint.Text = text.ChoiceHint;
        ApplyToAll.Content = context.Conflicts.Count > 0
            ? $"Apply to {context.Conflicts.Count:N0} {(context.Conflicts.Count == 1 ? "conflict" : "conflicts")} and any further conflicts in this transfer"
            : "Apply to all remaining conflicts";
    }

    public void SetComparisons(IReadOnlyList<TransferConflictComparison> comparisons)
    {
        ComparisonCards.Children.Clear();
        foreach (var comparison in comparisons)
        {
            var columns = new Grid { ColumnSpacing = 16 };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.Children.Add(FileCard("Incoming item", comparison.Incoming));
            var existing = FileCard("Existing item", comparison.Existing);
            Grid.SetColumn(existing, 1);
            columns.Children.Add(existing);
            var card = new StackPanel { Spacing = 6 };
            card.Children.Add(columns);
            card.Children.Add(new TextBlock { Text = comparison.Comparison, TextWrapping = TextWrapping.Wrap });
            card.Children.Add(new TextBlock
            {
                Text = comparison.KeepBothName.Length > 0 ? $"Keep both: {comparison.KeepBothName}" : "Keep both: no available name found",
                IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap,
            });
            ComparisonCards.Children.Add(card);
        }
        ChoiceHint.Text = "Keep both names are previews; another file created during the transfer may change the final name.";
    }

    private static StackPanel FileCard(string title, ConflictFileSnapshot file)
    {
        var card = new StackPanel { Spacing = 4 };
        card.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        card.Children.Add(new TextBlock { Text = file.Path, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        card.Children.Add(new TextBlock { Text = file.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        return card;
    }
}
