using System.Text.RegularExpressions;
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
}
