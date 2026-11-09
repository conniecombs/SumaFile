using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SumaFile.Remote.Model;

namespace SumaFile.Remote.Views;

/// <summary>
/// The body of the Connections sidebar section (the host draws its header, collapse button and the manager command
/// button). It lists the saved profiles as the 1.0.4 sidebar did, reloads when it is shown and on each
/// <c>remote.profiles-changed</c>, and opens the manager on the clicked profile. The host may drop this element and
/// ask for a new one; each instance unsubscribes when it leaves the tree.
/// </summary>
internal sealed class RemoteSidebarSection
{
    internal const string EmptyText = "No connections. Open the manager to add one.";

    private const string RowTemplate = """
        <DataTemplate>
            <Grid Padding="2,1" ColumnSpacing="8">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <FontIcon VerticalAlignment="Top" Margin="0,2,0,0" FontFamily="{ThemeResource SfIconFont}" FontSize="13" Foreground="{ThemeResource SfTextMutedBrush}" Glyph="&#xE968;" AutomationProperties.AccessibilityView="Raw" />
                <StackPanel Grid.Column="1" Spacing="2">
                    <TextBlock x:Name="NameText" VerticalAlignment="Center" Foreground="{ThemeResource SfTextPrimaryBrush}" TextTrimming="CharacterEllipsis" TextWrapping="NoWrap" />
                    <TextBlock x:Name="DescriptionText" FontSize="11" Foreground="{ThemeResource SfTextMutedBrush}" TextTrimming="CharacterEllipsis" TextWrapping="NoWrap" />
                </StackPanel>
                <StackPanel Grid.Column="2" VerticalAlignment="Top" Orientation="Horizontal" Spacing="4">
                    <Border Padding="5,1" Background="{ThemeResource SfAccentSoftBrush}" BorderThickness="0" CornerRadius="4">
                        <TextBlock x:Name="ProtocolText" FontSize="10" Foreground="{ThemeResource SfTextPrimaryBrush}" />
                    </Border>
                    <Border x:Name="InsecureBadge" Padding="5,0" BorderBrush="{ThemeResource SystemFillColorCriticalBrush}" BorderThickness="1" CornerRadius="4" Visibility="Collapsed" ToolTipService.ToolTip="Plain FTP: passwords and files are sent without encryption">
                        <TextBlock x:Name="InsecureText" FontSize="10" Foreground="{ThemeResource SystemFillColorCriticalBrush}" Text="Insecure" />
                    </Border>
                </StackPanel>
                <FontIcon Grid.Column="3" VerticalAlignment="Center" AutomationProperties.AccessibilityView="Raw" FontFamily="{ThemeResource SfIconFont}" FontSize="11" Foreground="{ThemeResource SfTextMutedBrush}" Glyph="&#xE8A7;" ToolTipService.ToolTip="Opens in the connections window" />
            </Grid>
        </DataTemplate>
        """;

    private readonly RemotePlugin _plugin;
    private readonly IRemoteBackend _backend;
    private readonly TextBlock _empty;
    private readonly TextBlock _error;
    private readonly ListView _list;
    private IDisposable? _subscription;
    private int _generation;

    public RemoteSidebarSection(RemotePlugin plugin, IRemoteBackend backend)
    {
        _plugin = plugin;
        _backend = backend;

        _empty = new TextBlock
        {
            Text = EmptyText,
            TextWrapping = TextWrapping.Wrap,
            Style = Xaml.Resource<Style>("SfEmptyHintStyle"),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_empty, RemoteAutomationIds.SidebarEmpty);

        _error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Style = Xaml.Resource<Style>("SfEmptyHintStyle"),
            Visibility = Visibility.Collapsed,
        };

        _list = new ListView
        {
            IsItemClickEnabled = true,
            SelectionMode = ListViewSelectionMode.None,
            Style = Xaml.Resource<Style>("SfSidebarListStyle"),
            ItemContainerStyle = Xaml.Resource<Style>("SfNavListItemStyle"),
        };
        AutomationProperties.SetAutomationId(_list, RemoteAutomationIds.SidebarList);
        AutomationProperties.SetName(_list, "Connections");
        ListRows.Attach<RemoteProfileRow>(_list, Xaml.Load<DataTemplate>(RowTemplate), FillRow);
        _list.ItemClick += OnItemClick;

        Root = new StackPanel { Spacing = 2 };
        Root.Children.Add(_empty);
        Root.Children.Add(_error);
        Root.Children.Add(_list);
        Root.Loaded += OnLoaded;
        Root.Unloaded += OnUnloaded;
    }

    /// <summary>The element the host places under the section header.</summary>
    public StackPanel Root { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscription is null)
        {
            var queue = Root.DispatcherQueue;
            _subscription = _plugin.TrackSubscription(_backend.OnProfilesChanged(() =>
                queue.TryEnqueue(DispatcherQueuePriority.Normal, () => _ = ReloadAsync())));
        }

        _ = ReloadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    /// <summary>Lists the profiles; only the latest of overlapping reloads updates the section.</summary>
    private async Task ReloadAsync()
    {
        var generation = ++_generation;
        try
        {
            var profiles = await _backend.ListProfilesAsync(CancellationToken.None);
            if (generation != _generation)
            {
                return;
            }

            Show(profiles
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(profile => profile.Host, StringComparer.OrdinalIgnoreCase)
                .Select(RemoteProfileRow.From)
                .ToList());
        }
        catch (Exception exception)
        {
            if (generation != _generation)
            {
                return;
            }

            _list.ItemsSource = null;
            _empty.Visibility = Visibility.Collapsed;
            _error.Text = "Could not list connections: " + exception.Message;
            _error.Visibility = Visibility.Visible;
        }
    }

    private void Show(IReadOnlyList<RemoteProfileRow> rows)
    {
        _error.Visibility = Visibility.Collapsed;
        _list.ItemsSource = rows;
        _empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Fills a realized row; its container carries the profile name, id and description.</summary>
    private static void FillRow(SelectorItem container, FrameworkElement row, RemoteProfileRow profile)
    {
        Xaml.Find<TextBlock>(row, "NameText").Text = profile.Name;
        Xaml.Find<TextBlock>(row, "DescriptionText").Text = profile.Description;
        Xaml.Find<TextBlock>(row, "ProtocolText").Text = profile.Protocol;
        Xaml.Find<Border>(row, "InsecureBadge").Visibility = profile.IsInsecure ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(container, profile.Name);
        ToolTipService.SetToolTip(container, profile.Description);
        container.Tag = profile.ProfileId;
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (ListRows.Clicked<RemoteProfileRow>(_list, e.ClickedItem) is { } row)
        {
            _plugin.OpenManager(row.ProfileId);
        }
    }
}
