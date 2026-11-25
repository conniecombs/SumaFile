using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Documents;
using SumaFile.Git.Model;

namespace SumaFile.Git.Views;

/// <summary>
/// The workbench panel's body, the 1.0.4 <c>GitWorkbenchView</c> built from <c>XamlReader</c> fragments (spec §5.3).
/// The host draws the title, the dock or window toggle, the close button and the resize grip; this body has the
/// branch, the summary, the action buttons, the changes list and the diff preview. Values come from the view model's
/// <see cref="INotifyPropertyChanged.PropertyChanged"/>, never from bindings. One instance per presentation: the host
/// asks again when the panel moves into or out of its window.
/// </summary>
internal sealed class GitWorkbenchPanel
{
    private const string Layout = """
        <Grid x:Name="Root" Padding="12" Background="{ThemeResource SfBgSecondaryBrush}" RowSpacing="10">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
            </Grid.RowDefinitions>
            <Grid ColumnSpacing="12">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <FontIcon VerticalAlignment="Center" FontFamily="{ThemeResource SfIconFont}" FontSize="16" Foreground="{ThemeResource SfAccentBrush}" Glyph="&#xE8EF;" AutomationProperties.AccessibilityView="Raw" />
                <StackPanel Grid.Column="1" MinWidth="0" Spacing="1">
                    <TextBlock x:Name="BranchText" FontSize="13" FontWeight="SemiBold" Foreground="{ThemeResource SfTextPrimaryBrush}" TextTrimming="CharacterEllipsis" />
                    <TextBlock x:Name="SummaryText" FontSize="12" Foreground="{ThemeResource SfTextMutedBrush}" TextTrimming="CharacterEllipsis" />
                </StackPanel>
                <ScrollViewer Grid.Column="2" HorizontalScrollBarVisibility="Auto" HorizontalScrollMode="Enabled" VerticalScrollBarVisibility="Disabled" VerticalScrollMode="Disabled">
                    <StackPanel Orientation="Horizontal" Spacing="6">
                        <Button x:Name="RefreshButton" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Refresh Git status" ToolTipService.ToolTip="Refresh Git status">
                            <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="12" Glyph="&#xE72C;" />
                        </Button>
                        <Button x:Name="FetchButton" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Fetch" ToolTipService.ToolTip="Fetch">
                            <FontIcon FontFamily="{ThemeResource SfIconFont}" FontSize="12" Glyph="&#xE8B5;" />
                        </Button>
                        <Button x:Name="PullButton" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Pull" ToolTipService.ToolTip="Pull">
                            <TextBlock FontSize="12" Text="Pull" />
                        </Button>
                        <Button x:Name="PushButton" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Push" ToolTipService.ToolTip="Push">
                            <TextBlock FontSize="12" Text="Push" />
                        </Button>
                        <Button x:Name="CommitButton" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Commit staged changes" ToolTipService.ToolTip="Commit staged changes">
                            <TextBlock FontSize="12" Text="Commit" />
                        </Button>
                        <Button x:Name="StageButton" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Stage selected changes" ToolTipService.ToolTip="Stage selected changes">
                            <TextBlock FontSize="12" Text="Stage" />
                        </Button>
                        <Button x:Name="UnstageButton" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Unstage selected changes" ToolTipService.ToolTip="Unstage selected changes">
                            <TextBlock FontSize="12" Text="Unstage" />
                        </Button>
                        <Button x:Name="DiffButton" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Preview selected diff" ToolTipService.ToolTip="Preview selected diff">
                            <TextBlock FontSize="12" Text="Diff" />
                        </Button>
                        <Border Width="1" Margin="2,4" Background="{ThemeResource SfBorderBrush}" />
                        <Button x:Name="DiscardButton" Style="{ThemeResource SfGhostButtonStyle}" Foreground="{ThemeResource SystemFillColorCriticalBrush}" AutomationProperties.Name="Discard selected changes" ToolTipService.ToolTip="Discard selected changes. This cannot be undone.">
                            <TextBlock FontSize="12" Text="Discard" />
                        </Button>
                    </StackPanel>
                </ScrollViewer>
            </Grid>
            <Grid x:Name="ContentGrid" Grid.Row="1" MinHeight="120">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition x:Name="ChangesColumn" Width="2*" MinWidth="260" />
                    <ColumnDefinition x:Name="DividerColumn" Width="8" />
                    <ColumnDefinition x:Name="DiffColumn" Width="420" MinWidth="260" MaxWidth="960" />
                </Grid.ColumnDefinitions>
                <Grid RowSpacing="8">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="*" />
                        <RowDefinition Height="Auto" />
                    </Grid.RowDefinitions>
                    <ListView x:Name="ChangesList" SelectionMode="Extended" AutomationProperties.Name="Git changes" />
                    <TextBlock x:Name="EmptyText" Margin="8" VerticalAlignment="Top" FontSize="12" Foreground="{ThemeResource SfTextMutedBrush}" Text="No changed files" />
                    <Grid Grid.Row="1" ColumnSpacing="8">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <TextBox x:Name="CommitMessageBox" MaxHeight="96" AcceptsReturn="True" FontSize="12" PlaceholderText="Commit message (Ctrl+Enter commits)" Style="{ThemeResource SfTextBoxStyle}" TextWrapping="Wrap" ScrollViewer.VerticalScrollBarVisibility="Auto" AutomationProperties.Name="Commit message" />
                        <Button x:Name="CommitHereButton" Grid.Column="1" VerticalAlignment="Bottom" Style="{ThemeResource SfGhostButtonStyle}" AutomationProperties.Name="Commit staged changes with this message" ToolTipService.ToolTip="Commit staged changes with this message">
                            <TextBlock FontSize="12" Text="Commit" />
                        </Button>
                    </Grid>
                </Grid>
                <Border Grid.Column="2" Padding="10" Background="{ThemeResource SfBgPrimaryBrush}" BorderBrush="{ThemeResource SfBorderBrush}" BorderThickness="1" CornerRadius="6">
                    <Grid RowSpacing="8">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="*" />
                        </Grid.RowDefinitions>
                        <TextBlock x:Name="DiffTitleText" FontSize="12" FontWeight="SemiBold" Foreground="{ThemeResource SfTextPrimaryBrush}" TextTrimming="CharacterEllipsis" />
                        <ScrollViewer x:Name="DiffScroller" Grid.Row="1" HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Auto" IsTabStop="True" AutomationProperties.Name="Git diff">
                            <RichTextBlock x:Name="DiffTextBlock" FontFamily="Consolas" FontSize="12" Foreground="{ThemeResource SfTextPrimaryBrush}" IsTextSelectionEnabled="True" TextWrapping="NoWrap" AutomationProperties.Name="Git diff text" />
                        </ScrollViewer>
                    </Grid>
                </Border>
            </Grid>
        </Grid>
        """;

    private const string ChangeItemTemplate = """
        <DataTemplate>
            <Grid MinHeight="36" Padding="8,5" ColumnSpacing="10">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="96" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <TextBlock x:Name="StatusText" VerticalAlignment="Center" FontSize="12" Foreground="{ThemeResource SfTextPrimaryBrush}" TextTrimming="CharacterEllipsis" />
                <StackPanel Grid.Column="1" MinWidth="0" Spacing="1">
                    <TextBlock x:Name="PathText" FontSize="12" Foreground="{ThemeResource SfTextPrimaryBrush}" TextTrimming="CharacterEllipsis" />
                    <TextBlock x:Name="DetailText" FontSize="11" Foreground="{ThemeResource SfTextMutedBrush}" TextTrimming="CharacterEllipsis" />
                </StackPanel>
                <TextBlock x:Name="StageText" Grid.Column="2" VerticalAlignment="Center" FontSize="11" Foreground="{ThemeResource SfTextMutedBrush}" />
            </Grid>
        </DataTemplate>
        """;

    private readonly GitWorkbenchController _controller;
    private readonly GitWorkbenchViewModel _model;
    private readonly DispatcherQueue _dispatcher;
    private readonly TextBlock _branchText;
    private readonly TextBlock _summaryText;
    private readonly Button _refresh;
    private readonly Button _fetch;
    private readonly Button _pull;
    private readonly Button _push;
    private readonly Button _commit;
    private readonly Button _stage;
    private readonly Button _unstage;
    private readonly Button _diff;
    private readonly Button _discard;
    private readonly ListView _changes;
    private readonly TextBlock _emptyText;
    private readonly TextBlock _diffTitle;
    private readonly RichTextBlock _diffText;
    private readonly TextBox _commitBox;
    private readonly Button _commitHere;
    private bool _attached;
    private bool _syncingList;

    public GitWorkbenchPanel(GitWorkbenchController controller, GitWorkbenchViewModel model, bool detached)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _model = model ?? throw new ArgumentNullException(nameof(model));

        Root = Xaml.Load<Grid>(Layout);
        _dispatcher = Root.DispatcherQueue;
        AutomationProperties.SetAutomationId(Root, GitAutomationIds.Workbench);
        AutomationProperties.SetName(Root, detached ? "Git workbench window" : "Git workbench");

        _branchText = Xaml.Find<TextBlock>(Root, "BranchText", GitAutomationIds.Branch);
        _summaryText = Xaml.Find<TextBlock>(Root, "SummaryText", GitAutomationIds.Summary);
        _refresh = Xaml.Find<Button>(Root, "RefreshButton", GitAutomationIds.Refresh);
        _fetch = Xaml.Find<Button>(Root, "FetchButton", GitAutomationIds.Fetch);
        _pull = Xaml.Find<Button>(Root, "PullButton", GitAutomationIds.Pull);
        _push = Xaml.Find<Button>(Root, "PushButton", GitAutomationIds.Push);
        _commit = Xaml.Find<Button>(Root, "CommitButton", GitAutomationIds.Commit);
        _stage = Xaml.Find<Button>(Root, "StageButton", GitAutomationIds.Stage);
        _unstage = Xaml.Find<Button>(Root, "UnstageButton", GitAutomationIds.Unstage);
        _diff = Xaml.Find<Button>(Root, "DiffButton", GitAutomationIds.Diff);
        _discard = Xaml.Find<Button>(Root, "DiscardButton", GitAutomationIds.Discard);
        _changes = Xaml.Find<ListView>(Root, "ChangesList", GitAutomationIds.Changes);
        _emptyText = Xaml.Find<TextBlock>(Root, "EmptyText");
        _diffTitle = Xaml.Find<TextBlock>(Root, "DiffTitleText", GitAutomationIds.DiffTitle);
        _diffText = Xaml.Find<RichTextBlock>(Root, "DiffTextBlock", GitAutomationIds.DiffText);
        _commitBox = Xaml.Find<TextBox>(Root, "CommitMessageBox", GitAutomationIds.CommitBox);
        _commitHere = Xaml.Find<Button>(Root, "CommitHereButton", GitAutomationIds.CommitHere);

        var content = Xaml.Find<Grid>(Root, "ContentGrid");
        var splitter = new GitDiffSplitter(
            content,
            Xaml.Find<ColumnDefinition>(Root, "ChangesColumn"),
            Xaml.Find<ColumnDefinition>(Root, "DividerColumn"),
            Xaml.Find<ColumnDefinition>(Root, "DiffColumn"));
        Grid.SetColumn(splitter, 1);
        content.Children.Add(splitter);

        _ = ListRows.Attach<GitChangeRow>(_changes, Xaml.Load<DataTemplate>(ChangeItemTemplate), FillChangeRow);
        _changes.SelectionChanged += OnChangesSelectionChanged;

        _refresh.Click += (_, _) => Run(() => _controller.RefreshAsync(silent: false, CancellationToken.None));
        _fetch.Click += (_, _) => RunCommand(GitCommandIds.Fetch);
        _pull.Click += (_, _) => RunCommand(GitCommandIds.Pull);
        _push.Click += (_, _) => RunCommand(GitCommandIds.Push);
        _commit.Click += (_, _) => RunCommand(GitCommandIds.Commit);
        // The same command: with a message in the box it commits at once, and with none it asks in its dialog.
        _commitHere.Click += (_, _) => RunCommand(GitCommandIds.Commit);
        _commitBox.TextChanged += (_, _) => _model.CommitMessage = _commitBox.Text;
        _commitBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter
                && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
                && _model.CanCommit)
            {
                e.Handled = true;
                RunCommand(GitCommandIds.Commit);
            }
        };
        // The diff's colours are brushes of the theme they were made in.
        Root.ActualThemeChanged += (_, _) => ShowDiff();
        _stage.Click += (_, _) => RunCommand(GitCommandIds.StageSelected);
        _unstage.Click += (_, _) => RunCommand(GitCommandIds.UnstageSelected);
        _diff.Click += (_, _) => RunCommand(GitCommandIds.DiffSelected);
        _discard.Click += (_, _) => RunCommand(GitCommandIds.DiscardSelected);

        // Follow the view model only while shown: the host drops this body when the panel hides or moves, and a
        // subscription would keep the dropped element alive.
        Root.Loaded += (_, _) => Attach();
        Root.Unloaded += (_, _) => Detach();
        Apply(null);
    }

    /// <summary>The element the host shows (<see cref="SimpleFile.PluginSdk.IPanelProvider.CreatePanel"/>).</summary>
    public Grid Root { get; }

    private void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _model.PropertyChanged += OnModelPropertyChanged;
        Apply(null);
    }

    private void Detach()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        _model.PropertyChanged -= OnModelPropertyChanged;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var name = e.PropertyName;
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            Apply(name);
        }
        else
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_attached)
                {
                    Apply(name);
                }
            });
        }
    }

    /// <summary>Copies one view model value (or every one, for null) into its element.</summary>
    private void Apply(string? name)
    {
        try
        {
            bool Is(string property) => name is null || name == property;
            if (Is(nameof(GitWorkbenchViewModel.BranchText))) _branchText.Text = _model.BranchText;
            if (Is(nameof(GitWorkbenchViewModel.SummaryText))) _summaryText.Text = _model.SummaryText;
            if (Is(nameof(GitWorkbenchViewModel.DiffTitle))) _diffTitle.Text = _model.DiffTitle;
            if (Is(nameof(GitWorkbenchViewModel.DiffText))) ShowDiff();
            if (Is(nameof(GitWorkbenchViewModel.CommitMessage)) && !string.Equals(_commitBox.Text, _model.CommitMessage, StringComparison.Ordinal))
            {
                _commitBox.Text = _model.CommitMessage;
            }

            if (Is(nameof(GitWorkbenchViewModel.CanRefresh))) _refresh.IsEnabled = _model.CanRefresh;
            if (Is(nameof(GitWorkbenchViewModel.CanFetch))) _fetch.IsEnabled = _model.CanFetch;
            if (Is(nameof(GitWorkbenchViewModel.CanPull))) _pull.IsEnabled = _model.CanPull;
            if (Is(nameof(GitWorkbenchViewModel.CanPush))) _push.IsEnabled = _model.CanPush;
            if (Is(nameof(GitWorkbenchViewModel.CanCommit)))
            {
                _commit.IsEnabled = _model.CanCommit;
                _commitHere.IsEnabled = _model.CanCommit;
            }

            if (Is(nameof(GitWorkbenchViewModel.CanStage))) _stage.IsEnabled = _model.CanStage;
            if (Is(nameof(GitWorkbenchViewModel.CanUnstage))) _unstage.IsEnabled = _model.CanUnstage;
            if (Is(nameof(GitWorkbenchViewModel.CanDiscard))) _discard.IsEnabled = _model.CanDiscard;
            if (Is(nameof(GitWorkbenchViewModel.CanDiff))) _diff.IsEnabled = _model.CanDiff;
            if (Is(nameof(GitWorkbenchViewModel.Changes)) || Is(nameof(GitWorkbenchViewModel.ChangesEmpty)))
            {
                ShowChanges();
            }
        }
        catch (Exception)
        {
            // An element the host already tore down; the next presentation starts from the view model again.
        }
    }

    /// <summary>
    /// Shows the diff with its lines coloured by what they are: added, removed, a hunk's range, a file's header.
    /// A very long diff is shown as plain text, in one piece.
    /// </summary>
    private void ShowDiff()
    {
        var text = _model.DiffText;
        var lines = GitDiffLines.Parse(text);
        var paragraph = new Paragraph();
        if (lines.Count > GitDiffLines.MaxColoredLines)
        {
            paragraph.Inlines.Add(new Run { Text = text });
        }
        else
        {
            for (var index = 0; index < lines.Count; index++)
            {
                if (index > 0)
                {
                    paragraph.Inlines.Add(new LineBreak());
                }

                var run = new Run { Text = lines[index].Text };
                if (DiffBrush(lines[index].Kind) is { } brush)
                {
                    run.Foreground = brush;
                }

                paragraph.Inlines.Add(run);
            }
        }

        _diffText.Blocks.Clear();
        _diffText.Blocks.Add(paragraph);
    }

    /// <summary>A line kind's colour, from the host's theme; null leaves the text's own colour.</summary>
    private static Brush? DiffBrush(GitDiffLineKind kind) => kind switch
    {
        GitDiffLineKind.Added => Xaml.Brush("SfDecorationAddedBrush", "SystemFillColorSuccessBrush"),
        GitDiffLineKind.Removed => Xaml.Brush("SfDecorationDeletedBrush", "SystemFillColorCriticalBrush"),
        GitDiffLineKind.Hunk => Xaml.Brush("SfAccentBrush"),
        GitDiffLineKind.Header => Xaml.Brush("SfTextMutedBrush"),
        _ => null,
    };

    private void ShowChanges()
    {
        var changes = _model.Changes;
        if (!ReferenceEquals(_changes.ItemsSource, changes))
        {
            _syncingList = true;
            try
            {
                _changes.ItemsSource = changes;
            }
            finally
            {
                _syncingList = false;
            }
        }

        _emptyText.Visibility = _model.ChangesEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnChangesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingList)
        {
            return;
        }

        _controller.OnPanelSelectionChanged(_changes.SelectedItems.OfType<GitChangeRow>().ToArray());
    }

    private static void FillChangeRow(SelectorItem container, FrameworkElement row, GitChangeRow change)
    {
        Xaml.Find<TextBlock>(row, "StatusText").Text = change.StatusText;
        Xaml.Find<TextBlock>(row, "PathText").Text = change.Path;
        Xaml.Find<TextBlock>(row, "DetailText").Text = change.DetailText;
        Xaml.Find<TextBlock>(row, "StageText").Text = change.StageText;
        AutomationProperties.SetName(container, change.Path);
        container.Tag = change.AbsolutePath;
    }

    private void RunCommand(string commandId) => Run(() => _controller.RunFromPanelAsync(commandId, CancellationToken.None));

    /// <summary>Runs a button's work; WinUI event handlers must never let an exception escape.</summary>
    private static async void Run(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception)
        {
            // The controller reports its own failures; nothing else may end the App from a click.
        }
    }
}
