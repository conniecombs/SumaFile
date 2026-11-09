using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SumaFile.Remote.Model;

namespace SumaFile.Remote.Views;

/// <summary>
/// The manager's four lists (profiles, local files, remote files, finished transfers). Each binds a
/// <see cref="CoalescingSnapshot{T}"/> copy of its view model collection as <c>ItemsSource</c>, so WinUI virtualizes
/// it and a folder of tens of thousands of entries realizes only the rows in view. Each row template is parsed once;
/// <see cref="ListRows"/> fills the realized rows, whose container carries the automation name the row shows and a
/// <c>Tag</c> with the path (or, for a profile, the profile id). No bindings.
/// </summary>
internal sealed partial class RemoteManagerWindow
{
    private const string ProfileItemTemplate = """
        <DataTemplate>
            <StackPanel MinWidth="0" Spacing="3">
                <Grid ColumnSpacing="6">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <TextBlock x:Name="NameText" FontSize="13" FontWeight="SemiBold" Foreground="{ThemeResource SfTextPrimaryBrush}" TextTrimming="CharacterEllipsis" />
                    <Border x:Name="InsecureBadge" Grid.Column="1" Padding="5,0" VerticalAlignment="Center" BorderBrush="{ThemeResource SystemFillColorCriticalBrush}" BorderThickness="1" CornerRadius="3" Visibility="Collapsed" ToolTipService.ToolTip="Plain FTP: passwords and files are sent without encryption">
                        <TextBlock x:Name="InsecureText" FontSize="11" Foreground="{ThemeResource SystemFillColorCriticalBrush}" Text="Insecure" />
                    </Border>
                </Grid>
                <TextBlock x:Name="HostText" FontSize="12" Foreground="{ThemeResource SfTextMutedBrush}" TextTrimming="CharacterEllipsis" />
            </StackPanel>
        </DataTemplate>
        """;

    private const string EntryItemTemplate = """
        <DataTemplate>
            <Grid MinWidth="0" ColumnSpacing="10">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="96" />
                </Grid.ColumnDefinitions>
                <FontIcon x:Name="KindIcon" FontFamily="{ThemeResource SfIconFont}" FontSize="12" Foreground="{ThemeResource SfTextMutedBrush}" AutomationProperties.AccessibilityView="Raw" />
                <TextBlock x:Name="NameText" Grid.Column="1" FontSize="13" Foreground="{ThemeResource SfTextPrimaryBrush}" TextTrimming="CharacterEllipsis" />
                <TextBlock x:Name="ModifiedText" Grid.Column="2" HorizontalAlignment="Right" FontSize="12" Foreground="{ThemeResource SfTextMutedBrush}" TextTrimming="CharacterEllipsis" />
            </Grid>
        </DataTemplate>
        """;

    private const string TransferItemTemplate = """
        <DataTemplate>
            <Grid MinWidth="0" ColumnSpacing="10">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="82" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="90" />
                </Grid.ColumnDefinitions>
                <TextBlock x:Name="DirectionText" FontSize="12" Foreground="{ThemeResource SfTextMutedBrush}" />
                <TextBlock x:Name="NameText" Grid.Column="1" FontSize="12" Foreground="{ThemeResource SfTextPrimaryBrush}" TextTrimming="CharacterEllipsis" />
                <TextBlock x:Name="StatusText" Grid.Column="2" HorizontalAlignment="Right" FontSize="12" Foreground="{ThemeResource SfTextMutedBrush}" />
            </Grid>
        </DataTemplate>
        """;

    private const string FolderGlyph = "";
    private const string FileGlyph = "";

    private readonly ListView _profileList;
    private readonly ListView _localList;
    private readonly ListView _remoteList;
    private readonly ListView _transferList;
    private readonly List<Action> _listDetachers = [];
    private readonly List<IDisposable> _snapshots = [];
    private readonly HashSet<RemoteTransferRecord> _watchedRecords = [];
    private IReadOnlyList<RemoteProfile> _shownProfiles = [];

    // Set while the code changes a list or its selection, so the list's own SelectionChanged does not write back.
    private bool _syncingLists;

    private void AttachLists()
    {
        var entryTemplate = Xaml.Load<DataTemplate>(EntryItemTemplate);
        _listDetachers.Add(ListRows.Attach<RemoteProfile>(_profileList, Xaml.Load<DataTemplate>(ProfileItemTemplate), FillProfileRow));
        _listDetachers.Add(ListRows.Attach<RemoteFileEntry>(_localList, entryTemplate, FillEntryRow));
        _listDetachers.Add(ListRows.Attach<RemoteFileEntry>(_remoteList, entryTemplate, FillEntryRow));
        _listDetachers.Add(ListRows.Attach<RemoteTransferRecord>(_transferList, Xaml.Load<DataTemplate>(TransferItemTemplate), FillTransferRow));

        // The view model fills a listing one Add at a time; each list takes one copy per UI-thread turn.
        var queue = DispatcherQueue;
        bool Enqueue(Action action) => queue.TryEnqueue(() => action());
        _snapshots.Add(new CoalescingSnapshot<RemoteProfile>(_viewModel.Profiles, Enqueue, ShowProfiles));
        _snapshots.Add(new CoalescingSnapshot<RemoteFileEntry>(_viewModel.LocalEntries, Enqueue, entries => ShowRows(_localList, entries)));
        _snapshots.Add(new CoalescingSnapshot<RemoteFileEntry>(_viewModel.RemoteEntries, Enqueue, entries => ShowRows(_remoteList, entries)));
        _snapshots.Add(new CoalescingSnapshot<RemoteTransferRecord>(_viewModel.Transfers, Enqueue, ShowTransfers));

        _profileList.SelectionChanged += OnProfileSelectionChanged;
        _localList.ItemClick += OnLocalItemClick;
        _remoteList.ItemClick += OnRemoteItemClick;
        _listDetachers.Add(() =>
        {
            _profileList.SelectionChanged -= OnProfileSelectionChanged;
            _localList.ItemClick -= OnLocalItemClick;
            _remoteList.ItemClick -= OnRemoteItemClick;
        });
    }

    private void DetachLists()
    {
        foreach (var snapshot in _snapshots)
        {
            snapshot.Dispose();
        }

        _snapshots.Clear();
        foreach (var detach in _listDetachers)
        {
            detach();
        }

        _listDetachers.Clear();
        foreach (var record in _watchedRecords)
        {
            record.PropertyChanged -= OnTransferRecordChanged;
        }

        _watchedRecords.Clear();
    }

    /// <summary>Shows a new copy of a collection; the list realizes only the rows in view.</summary>
    private void ShowRows<T>(ListView list, IReadOnlyList<T> items)
    {
        if (IsClosed)
        {
            return;
        }

        var syncing = _syncingLists;
        _syncingLists = true;
        try
        {
            list.ItemsSource = items;
        }
        finally
        {
            _syncingLists = syncing;
        }
    }

    private void ShowProfiles(IReadOnlyList<RemoteProfile> profiles)
    {
        if (IsClosed)
        {
            return;
        }

        _shownProfiles = profiles;
        ShowRows(_profileList, profiles);
        SyncSelectedProfile();
    }

    /// <summary>Shows the finished transfers and follows each shown record's status.</summary>
    private void ShowTransfers(IReadOnlyList<RemoteTransferRecord> records)
    {
        if (IsClosed)
        {
            return;
        }

        var shown = records.ToHashSet();
        foreach (var record in _watchedRecords.Where(record => !shown.Contains(record)).ToArray())
        {
            record.PropertyChanged -= OnTransferRecordChanged;
            _watchedRecords.Remove(record);
        }

        foreach (var record in shown)
        {
            if (_watchedRecords.Add(record))
            {
                record.PropertyChanged += OnTransferRecordChanged;
            }
        }

        ShowRows(_transferList, records);
    }

    private void OnTransferRecordChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsClosed && sender is RemoteTransferRecord record && ListRows.RealizedRow(_transferList, record) is { } row)
        {
            ShowTransferStatus(row, record);
        }
    }

    private static void FillProfileRow(SelectorItem container, FrameworkElement row, RemoteProfile profile)
    {
        Xaml.Find<TextBlock>(row, "NameText").Text = profile.Name;
        Xaml.Find<TextBlock>(row, "HostText").Text = profile.Host;
        Xaml.Find<Border>(row, "InsecureBadge").Visibility =
            RemoteProfileRow.From(profile).IsInsecure ? Visibility.Visible : Visibility.Collapsed;
        Label(container, profile.Name, profile.Id);
    }

    private static void FillEntryRow(SelectorItem container, FrameworkElement row, RemoteFileEntry entry)
    {
        Xaml.Find<FontIcon>(row, "KindIcon").Glyph = entry.IsDir ? FolderGlyph : FileGlyph;
        Xaml.Find<TextBlock>(row, "NameText").Text = entry.Name;
        Xaml.Find<TextBlock>(row, "ModifiedText").Text = entry.Modified;
        Label(container, entry.Name, entry.Path);
    }

    private static void FillTransferRow(SelectorItem container, FrameworkElement row, RemoteTransferRecord record)
    {
        Xaml.Find<TextBlock>(row, "DirectionText").Text = record.Direction;
        Xaml.Find<TextBlock>(row, "NameText").Text = record.FileName;
        ShowTransferStatus(row, record);
        Label(container, record.FileName, string.IsNullOrEmpty(record.RemotePath) ? record.LocalPath : record.RemotePath);
    }

    private static void ShowTransferStatus(FrameworkElement row, RemoteTransferRecord record)
    {
        var status = Xaml.Find<TextBlock>(row, "StatusText");
        status.Text = record.Status;
        ToolTipService.SetToolTip(status, string.IsNullOrEmpty(record.Detail) ? null : record.Detail);
    }

    /// <summary>A recycled container shows another model each time: its automation name and tag follow it.</summary>
    private static void Label(SelectorItem container, string name, string tag)
    {
        AutomationProperties.SetName(container, name);
        container.Tag = tag;
    }

    /// <summary>Selects the view model's profile in the list (the 1.0.4 two-way SelectedItem binding, in code).</summary>
    private void SyncSelectedProfile()
    {
        var index = -1;
        if (_viewModel.SelectedProfile is { } selected)
        {
            for (var i = 0; i < _shownProfiles.Count; i++)
            {
                if (ReferenceEquals(_shownProfiles[i], selected))
                {
                    index = i;
                    break;
                }
            }
        }

        if (_profileList.SelectedIndex == index)
        {
            return;
        }

        var syncing = _syncingLists;
        _syncingLists = true;
        try
        {
            _profileList.SelectedIndex = index;
        }
        finally
        {
            _syncingLists = syncing;
        }
    }

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingLists || IsClosed)
        {
            return;
        }

        var index = _profileList.SelectedIndex;
        _viewModel.SelectedProfile = index >= 0 && index < _shownProfiles.Count ? _shownProfiles[index] : null;
    }

    private void OnLocalItemClick(object sender, ItemClickEventArgs e)
    {
        if (ListRows.Clicked<RemoteFileEntry>(_localList, e.ClickedItem) is { IsDir: true } entry)
        {
            _ = RunRemoteActionAsync(() => _viewModel.NavigateLocalEntryAsync(entry));
        }
    }

    private void OnRemoteItemClick(object sender, ItemClickEventArgs e)
    {
        if (ListRows.Clicked<RemoteFileEntry>(_remoteList, e.ClickedItem) is { IsDir: true } entry)
        {
            _ = RunRemoteActionAsync(() => _viewModel.NavigateRemoteEntryAsync(entry));
        }
    }

    private IReadOnlyList<RemoteFileEntry> SelectedLocalEntries() => SelectedModels<RemoteFileEntry>(_localList);

    private IReadOnlyList<RemoteFileEntry> SelectedRemoteEntries() => SelectedModels<RemoteFileEntry>(_remoteList);

    private static IReadOnlyList<T> SelectedModels<T>(ListView list) where T : class =>
        list.SelectedItems
            .Select(item => ListRows.Clicked<T>(list, item))
            .OfType<T>()
            .ToArray();
}
