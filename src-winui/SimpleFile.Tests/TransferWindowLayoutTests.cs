using System.Text.RegularExpressions;
using SimpleFile.Core;
using Xunit;
using static SimpleFile.Tests.UiAuditSprint3Tests;

namespace SimpleFile.Tests;

/// <summary>
/// The Transfers window: what a row and its Details section show for the files being copied (source-shape
/// guards; the rendering itself is exercised by the Windows UI smoke).
/// </summary>
public sealed class TransferWindowLayoutTests
{
    private static string Xaml() => Read("TransferProgressWindow.xaml");

    private static string Details(string xaml) => Slice(xaml, "Header=\"Details\"", "</Expander>");

    /// <summary>
    /// Tab goes through a row in the order it is read. Details is drawn under the row with Cancel, so it is declared
    /// after it: declared first, as it was, Tab reached Details and its Options before Cancel.
    /// </summary>
    [Fact]
    public void ARow_IsTabbedInTheOrderItIsRead_AndItsButtonsAreNamed()
    {
        var xaml = Xaml();
        var cancel = xaml.IndexOf("Click=\"OnCancelOperationClicked\"", StringComparison.Ordinal);
        var remove = xaml.IndexOf("Click=\"OnRemoveOperationClicked\"", StringComparison.Ordinal);
        var details = xaml.IndexOf("<Expander", StringComparison.Ordinal);
        Assert.True(cancel > 0 && remove > cancel && details > remove, "Cancel, Remove and then Details, in the markup's order.");
        Assert.DoesNotContain("TabIndex", xaml);
        // Details is still the row's fourth line.
        Assert.Contains("Grid.Row=\"3\"", Slice(xaml, "<Expander", ">"));

        Assert.Contains("AutomationProperties.Name=\"{Binding CancelLabel}\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding RemoveLabel}\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding OptionsLabel}\"", xaml);
        Assert.Contains("IsEnabled=\"{Binding CanRemove}\"", xaml);
        Assert.Contains("window.RemoveRequested += OnTransferRemoveRequested;", Read("MainWindow.FileOperations.cs"));
        Assert.Contains("window.RemoveRequested -= OnTransferRemoveRequested;", Read("MainWindow.FileOperations.cs"));
        Assert.Contains("_transfer?.Remove(operation);", Read("MainWindow.FileOperations.cs"));
    }

    private static string[] FittedTexts(string xaml) =>
        [.. Regex.Matches(xaml, "<local:FittedText[^>]*/>", RegexOptions.Singleline).Select(match => match.Value)];

    [Fact]
    public void TheRowNamesOneFileAndKeepsTheCountOfTheOthersWhole()
    {
        var xaml = Xaml();
        Assert.Contains("xmlns:local=\"using:SimpleFile.App\"", xaml);

        var line = Assert.Single(FittedTexts(xaml), text => text.Contains("{Binding CurrentItemText}"));
        // The count is the suffix, which FittedText never shortens; only the name is cut.
        Assert.Contains("Suffix=\"{Binding CurrentItemSuffix}\"", line);
        Assert.Contains("Kind=\"FileName\"", line);
        Assert.Contains("ToolTipService.ToolTip=\"{Binding CurrentItemLine}\"", line);
        // It is part of the row itself, visible with Details closed.
        Assert.DoesNotContain("{Binding CurrentItemText}", Details(xaml));
    }

    [Fact]
    public void DetailsListsTheActiveFilesUnderTheirSourceFolders()
    {
        var details = Details(Xaml());
        Assert.Contains("ItemsSource=\"{Binding ActiveFolders}\"", details);
        Assert.Contains("ItemsSource=\"{Binding Files}\"", details);
        Assert.DoesNotContain("ItemsSource=\"{Binding ActiveFiles}\"", details);

        var texts = FittedTexts(details);
        var folder = Assert.Single(texts, text => text.Contains("Text=\"{Binding Folder}\""));
        Assert.Contains("Kind=\"FolderPath\"", folder);
        Assert.Contains("ToolTipService.ToolTip=\"{Binding Folder}\"", folder);

        var name = Assert.Single(texts, text => text.Contains("Text=\"{Binding Name}\""));
        Assert.Contains("Kind=\"FileName\"", name);
        // The whole source path stays one hover away from a shortened name.
        Assert.Contains("ToolTipService.ToolTip=\"{Binding Path}\"", name);

        Assert.True(details.IndexOf(folder, StringComparison.Ordinal) < details.IndexOf(name, StringComparison.Ordinal),
            "The folder heading comes before the files under it.");
    }

    [Fact]
    public void DetailsKeepsEveryLineOneRowHighAndTheFiguresInOneColumn()
    {
        var details = Details(Xaml());
        var files = Slice(details, "ItemsSource=\"{Binding Files}\"", "</ItemsControl>");
        // A fixed column for "1.2 GB / 4.4 GB": the names beside it never shift as the figures change.
        Assert.Matches(new Regex("<ColumnDefinition Width=\"\\*\" /><ColumnDefinition Width=\"\\d+\" />"), files);
        Assert.Contains("Text=\"{Binding Progress}\"", files);
        Assert.DoesNotContain("TextWrapping=\"Wrap\"", files);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", details);
    }

    [Fact]
    public void DetailsShowsWhereAFileComesFromNotWhereItGoes()
    {
        var details = Details(Xaml());
        Assert.DoesNotContain("Destination", details);
        Assert.DoesNotContain("RouteText", details);
        // The route stays in the row's heading.
        Assert.Contains("Text=\"{Binding RouteText}\"", Xaml());
    }

    [Fact]
    public void APathCanBeCopiedFromAFolderHeadingOrAFileLine()
    {
        var details = Details(Xaml());
        Assert.Equal(2, Regex.Matches(details, "RightTapped=\"OnActivePathRightTapped\"").Count);
        Assert.Contains("Tag=\"{Binding Folder}\"", details);
        Assert.Contains("Tag=\"{Binding Path}\"", details);

        var code = Read("TransferProgressWindow.xaml.cs");
        var handler = Slice(code, "void OnActivePathRightTapped(", "private void OnCancelOperationClicked(");
        Assert.Contains("Text = \"Copy path\"", handler);
        Assert.Contains("package.SetText(path)", handler);
    }

    [Fact]
    public void FittedTextMeasuresAndLeavesTheRulesToCore()
    {
        var control = Read("FittedText.cs");
        Assert.Contains("protected override Size MeasureOverride(Size availableSize)", control);
        Assert.Contains("TextFit.FileName(text, Fits)", control);
        Assert.Contains("TextFit.FolderPath(text, Fits)", control);
        Assert.Contains("TextFit.End(text, Fits)", control);
        // The suffix is measured with every candidate and added back untouched.
        Assert.Contains("WidthOf(candidate + suffix) <= width", control);
        // A screen reader gets the whole text.
        Assert.Contains("AutomationProperties.SetName(control._text, whole)", control);
    }

    [Fact]
    public void DetailsPaneStretchesVerticallyAndLeavesTheHeightRulesToCore()
    {
        var xaml = Xaml();
        var details = Details(xaml);

        // No fixed ceiling caps the active-file list when the window grows taller.
        Assert.DoesNotContain("MaxHeight=\"280\"", details);
        Assert.DoesNotContain("MaxHeight=", details);
        Assert.DoesNotContain("<StackPanel Spacing=\"8\">", details);
        Assert.Contains("<RowDefinition Height=\"Auto\" />", details);
        Assert.Contains("<RowDefinition Height=\"*\" />", details);
        Assert.Contains("MinHeight=\"96\"", details);

        var expanderOpenTag = Slice(xaml, "<Expander", ">");
        Assert.Contains("VerticalAlignment=\"Stretch\"", expanderOpenTag);
        Assert.Contains("VerticalContentAlignment=\"Stretch\"", expanderOpenTag);
        Assert.Contains("Expanding=\"OnDetailsExpanderExpanding\"", expanderOpenTag);
        Assert.Contains("Collapsed=\"OnDetailsExpanderCollapsed\"", expanderOpenTag);
        Assert.Contains("SizeChanged=\"OnDetailsExpanderSizeChanged\"", expanderOpenTag);

        var listOpenTag = Slice(xaml, "<ListView", ">");
        Assert.Contains("SizeChanged=\"OnTransferListSizeChanged\"", listOpenTag);
        Assert.Contains("ContainerContentChanging=\"OnTransferContainerContentChanging\"", listOpenTag);

        var code = Read("TransferProgressWindow.xaml.cs");
        Assert.Contains("TransferWindowLayout.DetailsScrollHeight(", code);
        Assert.Contains("TransferWindowLayout.HeightMatches(", code);
        Assert.Contains("scrollViewer.Height = double.NaN;", code);
        Assert.Contains("Operations.CollectionChanged -= OnOperationsCollectionChanged;", code);
    }

    [Fact]
    public void DetailsScrollHeight_StretchesSingleExpandedCardAcrossWindowHeights()
    {
        double[] expandedChrome = [235];
        double[] noCollapsed = [];

        Assert.Equal(214, TransferWindowLayout.DetailsScrollHeight(450, expandedChrome, noCollapsed));
        Assert.Equal(414, TransferWindowLayout.DetailsScrollHeight(650, expandedChrome, noCollapsed));
        Assert.Equal(714, TransferWindowLayout.DetailsScrollHeight(950, expandedChrome, noCollapsed));

        // Shrinking the window floors the Details scroll area at MinDetailsHeight instead of collapsing to zero.
        Assert.Equal(TransferWindowLayout.MinDetailsHeight, TransferWindowLayout.DetailsScrollHeight(280, expandedChrome, noCollapsed));
    }

    [Fact]
    public void DetailsScrollHeight_SplitsViewportAcrossMultipleExpandedCards()
    {
        double[] twoExpanded = [235, 235];
        double[] noCollapsed = [];

        Assert.Equal(164, TransferWindowLayout.DetailsScrollHeight(800, twoExpanded, noCollapsed));
        Assert.Equal(264, TransferWindowLayout.DetailsScrollHeight(1000, twoExpanded, noCollapsed));
        Assert.Equal(TransferWindowLayout.MinDetailsHeight, TransferWindowLayout.DetailsScrollHeight(520, twoExpanded, noCollapsed));
    }

    [Fact]
    public void DetailsScrollHeight_FitsOneCollapsedSiblingAndCapsReserveWhenManyExist()
    {
        double[] expandedChrome = [235];

        // One collapsed sibling fits alongside the expanded card with no outer overflow.
        Assert.Equal(266, TransferWindowLayout.DetailsScrollHeight(650, expandedChrome, [148]));

        // Many queued/completed rows reserve at most CollapsedSiblingReserveCap so the expanded Details pane still scales 1:1.
        var manyCollapsed = Enumerable.Repeat(148.0, 10).ToArray();
        var atDefault = TransferWindowLayout.DetailsScrollHeight(650, expandedChrome, manyCollapsed);
        var atTaller = TransferWindowLayout.DetailsScrollHeight(1050, expandedChrome, manyCollapsed);
        Assert.Equal(254, atDefault);
        Assert.Equal(654, atTaller);
    }

    [Fact]
    public void DetailsScrollHeight_IsMonotonicAndHandlesUnmeasuredInputs()
    {
        Assert.True(double.IsNaN(TransferWindowLayout.DetailsScrollHeight(600, [], [148])));
        Assert.True(double.IsNaN(TransferWindowLayout.DetailsScrollHeight(0, [235], [])));
        Assert.True(double.IsNaN(TransferWindowLayout.DetailsScrollHeight(double.NaN, [235], [])));
        Assert.True(double.IsNaN(TransferWindowLayout.DetailsScrollHeight(double.PositiveInfinity, [235], [])));

        var previous = 0.0;
        for (var viewport = 100.0; viewport <= 2000.0; viewport += 7.0)
        {
            var current = TransferWindowLayout.DetailsScrollHeight(viewport, [235], [148, 148, 148]);
            Assert.True(current >= previous, $"Details height dropped from {previous} to {current} at viewport {viewport}.");
            previous = current;
        }

        Assert.True(TransferWindowLayout.HeightMatches(double.NaN, double.NaN));
        Assert.False(TransferWindowLayout.HeightMatches(double.NaN, 200));
        Assert.True(TransferWindowLayout.HeightMatches(200.2, 200.0));
        Assert.False(TransferWindowLayout.HeightMatches(200.0, 201.0));
    }
}
