using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SimpleFile.Core;
using Windows.UI;

namespace SimpleFile.App;

public sealed partial class FileRowView : UserControl
{
    private readonly Dictionary<string, TextBlock> _textCells = new(StringComparer.Ordinal);
    private string _renderedColumnKey = "";
    private Ellipse? _tagPip;
    private Image? _iconImage;
    private TextBlock? _nameText;
    private TextBlock? _metadataText;
    private TextBlock? _secondaryText;
    private CancellationTokenSource? _thumbnailCts;

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row),
        typeof(FileRow),
        typeof(FileRowView),
        new PropertyMetadata(null, OnRowChanged));

    public FileRowView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyThemeResources();
    }

    public FileRow? Row
    {
        get => (FileRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    private static void OnRowChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is FileRowView view)
        {
            view.ApplyRow();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ColumnLayoutHost.Changed += OnColumnsChanged;
        FileListViewHost.Changed += OnViewSettingsChanged;
        FileListThumbnailHost.Changed += OnThumbnailsChanged;
        ApplyColumns();
        ApplyRow();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ColumnLayoutHost.Changed -= OnColumnsChanged;
        FileListViewHost.Changed -= OnViewSettingsChanged;
        FileListThumbnailHost.Changed -= OnThumbnailsChanged;
        CancelThumbnailLoad();
    }

    private void OnColumnsChanged(object? sender, EventArgs e)
    {
        ApplyColumns();
    }

    private void OnViewSettingsChanged(object? sender, EventArgs e)
    {
        ApplyColumns();
        ApplyRow();
    }

    private void OnThumbnailsChanged(object? sender, EventArgs e)
    {
        ApplyRow();
    }

    private void ApplyRow()
    {
        if (Row is null)
        {
            return;
        }

        ApplyColumns();
        ApplyIcon();
        Opacity = Row.IsHidden ? 0.55 : 1;

        if (_nameText is not null)
        {
            _nameText.Text = Row.Name;
            ToolTipService.SetToolTip(_nameText, Row.Name);
        }

        var isTileView = FileListViewHost.ViewFor(Row.Pane) == "tiles";

        if (_metadataText is not null)
        {
            var value = isTileView ? TilePrimaryMetadataText(Row) : MetadataText(Row);
            _metadataText.Text = value;
            ToolTipService.SetToolTip(_metadataText, string.IsNullOrWhiteSpace(value) ? null : value);
        }

        if (_secondaryText is not null)
        {
            var value = isTileView ? Row.ModifiedText : SecondaryText(Row);
            _secondaryText.Text = value;
            ToolTipService.SetToolTip(_secondaryText, string.IsNullOrWhiteSpace(value) ? null : value);
        }

        foreach (var (id, text) in _textCells)
        {
            var value = Row.ColumnText(id);
            text.Text = value;
            ToolTipService.SetToolTip(text, string.IsNullOrWhiteSpace(value) ? null : value);
        }

        ApplyTagPip(Row.TagColor);
        Opacity = Row.IsCut ? 0.45 : 1.0;
        AutomationProperties.SetName(this, Row.AutomationName);
    }

    private void ApplyColumns()
    {
        var pane = Row?.Pane ?? PaneId.Primary;
        var columns = ColumnLayoutHost.For(pane);
        var visible = columns.VisibleColumns;
        var view = FileListViewHost.ViewFor(pane);
        var iconSize = FileListViewHost.IconSizeFor(pane);
        var columnKey = view == "details"
            ? string.Join('\u001f', visible.Select(column => column.Id))
            : "";
        var key = $"{view}:{iconSize}:{columnKey}";
        if (!string.Equals(_renderedColumnKey, key, StringComparison.Ordinal))
        {
            RebuildLayout(view, iconSize, visible);
            _renderedColumnKey = key;
        }

        if (view == "tiles")
        {
            var tileWidth = FileTileLayoutMetrics.ContentWidthFor(iconSize);
            RowGrid.Width = tileWidth;
            RowGrid.HorizontalAlignment = HorizontalAlignment.Left;
            MinWidth = tileWidth;
            return;
        }

        if (view != "details")
        {
            MinWidth = 0;
            RowGrid.Width = double.NaN;
            RowGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
            return;
        }

        var total = 0.0;
        for (var index = 0; index < visible.Count && index < RowGrid.ColumnDefinitions.Count; index++)
        {
            var column = visible[index];
            RowGrid.ColumnDefinitions[index].Width = new GridLength(column.Width);
            total += column.Width;
        }

        RowGrid.Width = total;
        RowGrid.HorizontalAlignment = HorizontalAlignment.Left;
        MinWidth = total;
    }

    private void ApplyIcon()
    {
        if (_iconImage is null || Row is null)
        {
            CancelThumbnailLoad();
            return;
        }

        var row = Row;
        var iconSize = FileListViewHost.IconSizeFor(row.Pane);
        _iconImage.Width = iconSize;
        _iconImage.Height = iconSize;
        CancelThumbnailLoad();
        _iconImage.Source = ShellIconLoader.ForEntry(row.Path, row.IsDir, iconSize);

        if (!FileListThumbnailHost.ShouldUseThumbnails(row, iconSize))
        {
            return;
        }

        var cached = FileListThumbnailHost.CachedThumbnail(row, iconSize);
        if (cached is not null)
        {
            _iconImage.Source = cached;
            return;
        }

        var cts = new CancellationTokenSource();
        _thumbnailCts = cts;
        _ = LoadThumbnailAsync(row, iconSize, cts);
    }

    private async Task LoadThumbnailAsync(FileRow row, int iconSize, CancellationTokenSource cts)
    {
        try
        {
            var source = await FileListThumbnailHost.LoadThumbnailAsync(row, iconSize, cts.Token);
            if (source is null || !IsCurrentThumbnailTarget(row, iconSize, cts.Token))
            {
                return;
            }

            _iconImage!.Source = source;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_thumbnailCts, cts))
            {
                _thumbnailCts = null;
            }

            cts.Dispose();
        }
    }

    private bool IsCurrentThumbnailTarget(FileRow row, int iconSize, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && _iconImage is not null
            && Row is not null
            && Row.Pane == row.Pane
            && string.Equals(Row.Path, row.Path, StringComparison.OrdinalIgnoreCase)
            && FileListViewHost.IconSizeFor(Row.Pane) == iconSize;
    }

    private void CancelThumbnailLoad()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts = null;
    }

    private void RebuildLayout(string view, int iconSize, IReadOnlyList<FileListColumn> columns)
    {
        ResetLayout();
        switch (view)
        {
            case "list":
                RebuildListLayout(iconSize);
                break;
            case "tiles":
                RebuildTileLayout(iconSize);
                break;
            case "content":
                RebuildContentLayout(iconSize);
                break;
            default:
                RebuildDetailsLayout(columns, iconSize);
                break;
        }
    }

    private void ResetLayout()
    {
        RowGrid.ColumnDefinitions.Clear();
        RowGrid.RowDefinitions.Clear();
        RowGrid.Children.Clear();
        RowGrid.Width = double.NaN;
        RowGrid.MinHeight = 28;
        RowGrid.ColumnSpacing = 10;
        MinWidth = 0;
        RowGrid.RowSpacing = 0;
        _textCells.Clear();
        _tagPip = null;
        _iconImage = null;
        _nameText = null;
        _metadataText = null;
        _secondaryText = null;
    }

    private void RebuildDetailsLayout(IReadOnlyList<FileListColumn> columns, int iconSize)
    {
        RowGrid.MinHeight = Math.Max(28, iconSize + 10);
        RowGrid.ColumnSpacing = 0;
        RowGrid.HorizontalAlignment = HorizontalAlignment.Left;

        var total = 0.0;
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            RowGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(column.Width),
            });
            total += column.Width;

            FrameworkElement cell = column.Id == "name"
                ? CreateNameCell(iconSize, wrapName: false)
                : CreateTextCell(column.Id);
            Grid.SetColumn(cell, index);
            RowGrid.Children.Add(cell);
        }

        RowGrid.Width = total;
        MinWidth = total;
    }

    private void RebuildListLayout(int iconSize)
    {
        RowGrid.MinHeight = Math.Max(30, iconSize + 10);
        RowGrid.ColumnSpacing = 8;
        RowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _tagPip = CreateTagPip();
        Grid.SetColumn(_tagPip, 0);
        RowGrid.Children.Add(_tagPip);

        _iconImage = CreateIcon(iconSize);
        Grid.SetColumn(_iconImage, 1);
        RowGrid.Children.Add(_iconImage);

        var text = new Grid { ColumnSpacing = 10 };
        text.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        text.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _nameText = CreateNameText(wrapName: false);
        _metadataText = CreateMetadataText();
        Grid.SetColumn(_nameText, 0);
        Grid.SetColumn(_metadataText, 1);
        text.Children.Add(_nameText);
        text.Children.Add(_metadataText);
        Grid.SetColumn(text, 2);
        RowGrid.Children.Add(text);
    }

    private void RebuildTileLayout(int iconSize)
    {
        if (FileTileLayoutMetrics.UsesStackedLayout(iconSize))
        {
            RebuildStackedTileLayout(iconSize);
            return;
        }

        RowGrid.Width = FileTileLayoutMetrics.ContentWidthFor(iconSize);
        RowGrid.MinHeight = FileTileLayoutMetrics.MinHeightFor(iconSize);
        RowGrid.ColumnSpacing = 10;
        RowGrid.HorizontalAlignment = HorizontalAlignment.Left;
        RowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconHost = new Grid();
        _iconImage = CreateIcon(iconSize);
        iconHost.Children.Add(_iconImage);
        _tagPip = CreateTagPip();
        _tagPip.HorizontalAlignment = HorizontalAlignment.Right;
        _tagPip.VerticalAlignment = VerticalAlignment.Top;
        iconHost.Children.Add(_tagPip);
        Grid.SetColumn(iconHost, 0);
        RowGrid.Children.Add(iconHost);

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
        };
        _nameText = CreateNameText(wrapName: true);
        _metadataText = CreateMetadataText();
        _secondaryText = CreateMetadataText(opacity: 0.74);
        stack.Children.Add(_nameText);
        stack.Children.Add(_metadataText);
        stack.Children.Add(_secondaryText);
        Grid.SetColumn(stack, 1);
        RowGrid.Children.Add(stack);
    }

    private void RebuildStackedTileLayout(int iconSize)
    {
        RowGrid.Width = FileTileLayoutMetrics.ContentWidthFor(iconSize);
        RowGrid.MinHeight = FileTileLayoutMetrics.MinHeightFor(iconSize);
        RowGrid.ColumnSpacing = 0;
        RowGrid.RowSpacing = 7;
        RowGrid.HorizontalAlignment = HorizontalAlignment.Left;
        RowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        RowGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var iconHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _iconImage = CreateIcon(iconSize);
        iconHost.Children.Add(_iconImage);
        _tagPip = CreateTagPip();
        _tagPip.HorizontalAlignment = HorizontalAlignment.Right;
        _tagPip.VerticalAlignment = VerticalAlignment.Top;
        iconHost.Children.Add(_tagPip);
        Grid.SetRow(iconHost, 0);
        RowGrid.Children.Add(iconHost);

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 2,
        };
        _nameText = CreateNameText(wrapName: true);
        _nameText.HorizontalAlignment = HorizontalAlignment.Stretch;
        _nameText.TextAlignment = TextAlignment.Center;
        _metadataText = CreateMetadataText();
        _metadataText.HorizontalAlignment = HorizontalAlignment.Stretch;
        _metadataText.TextAlignment = TextAlignment.Center;
        _secondaryText = CreateMetadataText(opacity: 0.74);
        _secondaryText.HorizontalAlignment = HorizontalAlignment.Stretch;
        _secondaryText.TextAlignment = TextAlignment.Center;
        stack.Children.Add(_nameText);
        stack.Children.Add(_metadataText);
        stack.Children.Add(_secondaryText);
        Grid.SetRow(stack, 1);
        RowGrid.Children.Add(stack);
    }

    private void RebuildContentLayout(int iconSize)
    {
        RowGrid.MinHeight = Math.Max(54, iconSize + 14);
        RowGrid.ColumnSpacing = 12;
        RowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconHost = new Grid();
        _iconImage = CreateIcon(iconSize);
        iconHost.Children.Add(_iconImage);
        _tagPip = CreateTagPip();
        _tagPip.HorizontalAlignment = HorizontalAlignment.Right;
        _tagPip.VerticalAlignment = VerticalAlignment.Top;
        iconHost.Children.Add(_tagPip);
        Grid.SetColumn(iconHost, 0);
        RowGrid.Children.Add(iconHost);

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
        };
        _nameText = CreateNameText(wrapName: false);
        _metadataText = CreateMetadataText();
        _secondaryText = CreateMetadataText(opacity: 0.74);
        stack.Children.Add(_nameText);
        stack.Children.Add(_metadataText);
        stack.Children.Add(_secondaryText);
        Grid.SetColumn(stack, 1);
        RowGrid.Children.Add(stack);
    }

    private Grid CreateNameCell(int iconSize, bool wrapName)
    {
        var cell = new Grid
        {
            ColumnSpacing = 9,
        };
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _tagPip = CreateTagPip();
        Grid.SetColumn(_tagPip, 0);
        cell.Children.Add(_tagPip);

        _iconImage = CreateIcon(iconSize);
        Grid.SetColumn(_iconImage, 1);
        cell.Children.Add(_iconImage);

        _nameText = CreateNameText(wrapName);
        Grid.SetColumn(_nameText, 2);
        cell.Children.Add(_nameText);
        return cell;
    }

    private static Ellipse CreateTagPip()
    {
        return new Ellipse
        {
            Width = 7,
            Height = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
    }

    private static Image CreateIcon(int iconSize)
    {
        return new Image
        {
            Width = iconSize,
            Height = iconSize,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
        };
    }

    private TextBlock CreateNameText(bool wrapName)
    {
        return new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            Foreground = Brush("SfTextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = wrapName ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MaxLines = wrapName ? 2 : 1,
        };
    }

    private FrameworkElement CreateTextCell(string id)
    {
        var text = CreateMetadataText();
        text.Margin = new Thickness(10, 0, 8, 0);
        _textCells[id] = text;
        return text;
    }

    private TextBlock CreateMetadataText(double opacity = 0.9)
    {
        return new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = Brush("SfTextMutedBrush"),
            Opacity = opacity,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
    }

    private static string MetadataText(FileRow row)
    {
        var sizeOrItems = row.IsDir && !string.IsNullOrWhiteSpace(row.ItemsText) ? row.ItemsText : row.SizeText;
        return string.Join("  ", new[] { sizeOrItems, row.TypeText, row.ModifiedText }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string TilePrimaryMetadataText(FileRow row)
    {
        var sizeOrItems = row.IsDir && !string.IsNullOrWhiteSpace(row.ItemsText) ? row.ItemsText : row.SizeText;
        return string.Join("  ", new[] { sizeOrItems, row.TypeText }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string SecondaryText(FileRow row)
    {
        return string.Join("  ", new[] { row.PathText, row.GitText, row.SymlinkText }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private void ApplyTagPip(string color)
    {
        var brush = TryBrush(color);
        if (brush is null)
        {
            if (_tagPip is not null)
            {
                _tagPip.Visibility = Visibility.Collapsed;
            }

            return;
        }

        if (_tagPip is null)
        {
            return;
        }

        _tagPip.Fill = brush;
        _tagPip.Visibility = Visibility.Visible;
    }

    private void ApplyThemeResources()
    {
        if (_nameText is not null)
        {
            _nameText.Foreground = Brush("SfTextPrimaryBrush");
        }

        if (_metadataText is not null)
        {
            _metadataText.Foreground = Brush("SfTextMutedBrush");
        }

        if (_secondaryText is not null)
        {
            _secondaryText.Foreground = Brush("SfTextMutedBrush");
        }

        foreach (var text in _textCells.Values)
        {
            text.Foreground = Brush("SfTextMutedBrush");
        }
    }

    private Brush Brush(string key)
    {
        return ThemeResourceLookup.Brush(this, key);
    }

    private static Brush? TryBrush(string color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        try
        {
            var hex = color.Trim().TrimStart('#');
            if (hex.Length != 6)
            {
                return null;
            }

            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex[2..4], 16);
            var b = Convert.ToByte(hex[4..6], 16);
            return new SolidColorBrush(Color.FromArgb(255, r, g, b));
        }
        catch
        {
            return null;
        }
    }
}

public static class ColumnLayoutHost
{
    public static event EventHandler? Changed;

    private static ColumnLayout _primary = new();
    private static ColumnLayout _secondary = new();

    /// <summary>Legacy alias for the primary pane layout. Prefer <see cref="For"/>.</summary>
    public static ColumnLayout Shared => _primary;

    public static ColumnLayout For(PaneId pane) =>
        pane == PaneId.Secondary ? _secondary : _primary;

    public static void Attach(ColumnLayout primary, ColumnLayout secondary)
    {
        Unhook(_primary);
        Unhook(_secondary);
        _primary = primary;
        _secondary = secondary;
        Hook(_primary);
        Hook(_secondary);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Detach(ColumnLayout primary, ColumnLayout secondary)
    {
        Unhook(primary);
        Unhook(secondary);
        if (ReferenceEquals(_primary, primary))
        {
            _primary = new ColumnLayout();
        }

        if (ReferenceEquals(_secondary, secondary))
        {
            _secondary = new ColumnLayout();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void Hook(ColumnLayout layout) => layout.Changed += OnLayoutChanged;

    private static void Unhook(ColumnLayout layout) => layout.Changed -= OnLayoutChanged;

    private static void OnLayoutChanged(object? sender, EventArgs e) => Changed?.Invoke(sender, e);
}

public static class FileListViewHost
{
    public static event EventHandler? Changed;

    private static string _primaryView = UiSettings.NormalizeDefaultView(null);
    private static string _secondaryView = UiSettings.NormalizeDefaultView(null);
    private static int _primaryIconSize = UiSettings.NormalizeIconSize((int?)null);
    private static int _secondaryIconSize = UiSettings.NormalizeIconSize((int?)null);

    public static string ViewFor(PaneId pane) =>
        pane == PaneId.Secondary ? _secondaryView : _primaryView;

    public static int IconSizeFor(PaneId pane) =>
        pane == PaneId.Secondary ? _secondaryIconSize : _primaryIconSize;

    public static void Apply(PaneId pane, string? view, int iconSize)
    {
        var nextView = UiSettings.NormalizeDefaultView(view);
        var nextIconSize = UiSettings.NormalizeIconSize(iconSize);
        if (pane == PaneId.Secondary)
        {
            if (string.Equals(_secondaryView, nextView, StringComparison.Ordinal) && _secondaryIconSize == nextIconSize)
            {
                return;
            }

            _secondaryView = nextView;
            _secondaryIconSize = nextIconSize;
        }
        else
        {
            if (string.Equals(_primaryView, nextView, StringComparison.Ordinal) && _primaryIconSize == nextIconSize)
            {
                return;
            }

            _primaryView = nextView;
            _primaryIconSize = nextIconSize;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }
}
