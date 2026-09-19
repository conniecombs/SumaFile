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
    }
}
