using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.PluginSdk;
using SumaFile.Git.Model;

namespace SumaFile.Git.Views;

/// <summary>
/// The commit and discard dialogs, code-built <see cref="ContentDialog"/>s shown through the host's dialog gate
/// (<see cref="IPluginDialogs.ShowDialogAsync"/>). Each opens on the shown workbench's own <see cref="XamlRoot"/>,
/// docked or in its window, or on the main window (a null root) when no workbench is shown.
/// </summary>
internal sealed class GitPrompts(IPluginHost host, Func<XamlRoot?> shownPanelXamlRoot) : IGitPrompts
{
    private const string CriticalPrimaryButtonStyle = """
        <Style TargetType="Button">
            <Setter Property="Background" Value="{ThemeResource SystemFillColorCriticalBrush}" />
            <Setter Property="BorderBrush" Value="{ThemeResource SystemFillColorCriticalBrush}" />
            <Setter Property="Foreground" Value="{ThemeResource TextOnAccentFillColorPrimaryBrush}" />
        </Style>
        """;

    private readonly IPluginHost _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly Func<XamlRoot?> _shownPanelXamlRoot = shownPanelXamlRoot ?? throw new ArgumentNullException(nameof(shownPanelXamlRoot));

    public async Task<string?> PromptCommitMessageAsync(CancellationToken ct)
    {
        var input = new TextBox
        {
            PlaceholderText = "Commit message",
            MinWidth = 360,
        };
        AutomationProperties.SetAutomationId(input, GitAutomationIds.CommitMessage);
        AutomationProperties.SetName(input, "Commit message");
        input.Loaded += (_, _) => input.Focus(FocusState.Programmatic);
        var dialog = new ContentDialog
        {
            XamlRoot = _shownPanelXamlRoot(),
            Title = "Git commit",
            Content = input,
            PrimaryButtonText = "Commit",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await _host.Dialogs.ShowDialogAsync(dialog, ct);
        return result == PluginDialogResult.Primary ? input.Text : null;
    }

    public async Task<bool> ConfirmDiscardAsync(IReadOnlyList<GitChangeRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var dialog = new ContentDialog
        {
            XamlRoot = _shownPanelXamlRoot(),
            Title = rows.Count == 1 ? "Discard this change?" : $"Discard {rows.Count} changes?",
            Content = new TextBlock
            {
                Text = GitText.DiscardConfirmationText(rows),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
            PrimaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonStyle = Xaml.Load<Style>(CriticalPrimaryButtonStyle),
        };

        return await _host.Dialogs.ShowDialogAsync(dialog, ct) == PluginDialogResult.Primary;
    }
}
