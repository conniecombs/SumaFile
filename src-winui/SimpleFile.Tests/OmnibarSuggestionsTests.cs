using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>What the omnibar and a pane's path box suggest while typing (audit U6).</summary>
public class OmnibarSuggestionsTests
{
    private const string Here = @"C:\Users\test";

    [Fact]
    public void Request_APathAsksForTheFoldersOfItsParent()
    {
        var typed = OmnibarSuggestions.Request(@"C:\Users\te", OmnibarMode.Navigate, Here);
        Assert.Equal(new OmnibarSuggestionRequest(OmnibarSuggestionKind.Folder, @"C:\Users", "te"), typed);

        // After a separator, every folder inside.
        var inside = OmnibarSuggestions.Request(@"C:\Users\", OmnibarMode.Navigate, Here);
        Assert.Equal(new OmnibarSuggestionRequest(OmnibarSuggestionKind.Folder, @"C:\Users", ""), inside);
    }

    [Fact]
    public void Request_ABareNameIsAFolderOfTheOneBeingShown()
    {
        Assert.Equal(
            new OmnibarSuggestionRequest(OmnibarSuggestionKind.Folder, Here, "Doc"),
            OmnibarSuggestions.Request("Doc", OmnibarMode.Navigate, Here));
        // Not in the Recycle Bin, which has no folders to go into, and not without a current folder.
        Assert.Equal(OmnibarSuggestionRequest.None, OmnibarSuggestions.Request("Doc", OmnibarMode.Navigate, PathRules.RecycleBinPath));
        Assert.Equal(OmnibarSuggestionRequest.None, OmnibarSuggestions.Request("Doc", OmnibarMode.Navigate, ""));
        // A drive letter alone is not a name here.
        Assert.Equal(OmnibarSuggestionRequest.None, OmnibarSuggestions.Request("D:", OmnibarMode.Navigate, Here));
    }

    [Fact]
    public void Request_CommandsAfterTheirPrefixOrInCommandMode()
    {
        var prefixed = OmnibarSuggestions.Request("> refr", OmnibarMode.Navigate, Here);
        Assert.Equal(OmnibarSuggestionKind.Command, prefixed.Kind);
        Assert.Equal("refr", prefixed.Query);

        var inMode = OmnibarSuggestions.Request("refr", OmnibarMode.Command, Here);
        Assert.Equal(OmnibarSuggestionKind.Command, inMode.Kind);
        Assert.Equal("refr", inMode.Query);

        // Nothing typed after the prefix: the whole catalog is not a suggestion.
        Assert.Equal(OmnibarSuggestionRequest.None, OmnibarSuggestions.Request(">", OmnibarMode.Navigate, Here));
        Assert.Equal(OmnibarSuggestionRequest.None, OmnibarSuggestions.Request("", OmnibarMode.Command, Here));
    }

    [Theory]
    [InlineData("?report", OmnibarMode.Navigate)]
    [InlineData("~report", OmnibarMode.Navigate)]
    [InlineData("report", OmnibarMode.Search)]
    [InlineData("report", OmnibarMode.Filter)]
    [InlineData(@"C:\Users\te", OmnibarMode.Search)]
    public void Request_ASearchOrAFilterGetsNoSuggestions(string text, OmnibarMode mode)
    {
        // Their results are the file list itself.
        Assert.Equal(OmnibarSuggestionRequest.None, OmnibarSuggestions.Request(text, mode, Here));
    }

    [Fact]
    public void Request_AHomeRelativePathIsAPathAndNotAFilter()
    {
        // OmnibarIntentParser reads "~\folder" as a path too.
        Assert.Equal(OmnibarSuggestionKind.Folder, OmnibarSuggestions.Request(@"~\Doc", OmnibarMode.Navigate, Here).Kind);
    }

    [Fact]
    public void FolderSuggestions_AreTheFoldersThatStartWithWhatIsTyped()
    {
        string[] folders = [@"C:\Users\test", @"C:\Users\Temp", @"C:\Users\Public", @"C:\Users\test"];

        var suggestions = OmnibarSuggestions.FolderSuggestions(folders, "te");

        Assert.Equal([@"C:\Users\test", @"C:\Users\Temp"], suggestions.Select(suggestion => suggestion.Value));
        Assert.All(suggestions, suggestion => Assert.Equal(OmnibarSuggestionKind.Folder, suggestion.Kind));
        Assert.All(suggestions, suggestion => Assert.Equal(suggestion.Value, suggestion.Text));
        Assert.Equal(OmnibarSuggestions.MaxFolders, OmnibarSuggestions.FolderSuggestions(Enumerable.Range(0, 40).Select(n => $@"C:\f{n}"), "f").Count);
    }

    [Fact]
    public void CommandSuggestions_PutTheLabelsThatStartWithTheQueryFirst()
    {
        AppCommand[] commands =
        [
            new("layout-reset", "Reset layout", "View"),
            new("rename", "Rename", "File"),
            new("advanced-rename", "Advanced rename", "File"),
            new("re-scan", "Scan again", "Plugins"),
            new("copy", "Copy", "Edit"),
        ];

        var suggestions = OmnibarSuggestions.CommandSuggestions("re", commands);

        // Starts with it, then a word that starts with it, then a match elsewhere (here in the id); Copy has none.
        Assert.Equal(["layout-reset", "rename", "advanced-rename", "re-scan"], suggestions.Select(suggestion => suggestion.Value));
        Assert.Equal("Reset layout  (View)", suggestions[0].Text);
        Assert.All(suggestions, suggestion => Assert.Equal(OmnibarSuggestionKind.Command, suggestion.Kind));
        Assert.Empty(OmnibarSuggestions.CommandSuggestions("  ", commands));
    }

    [Fact]
    public void CommandSuggestions_ComeFromTheCatalogAndAreFew()
    {
        var suggestions = OmnibarSuggestions.CommandSuggestions("refresh");

        Assert.Equal("refresh", suggestions[0].Value);
        Assert.True(OmnibarSuggestions.CommandSuggestions("e").Count <= OmnibarSuggestions.MaxCommands);
    }

    [Fact]
    public void AcceptingAFolder_LeavesTheBoxReadyForTheNextName()
    {
        Assert.Equal(@"C:\Users\test\", OmnibarSuggestions.AcceptedFolderText(@"C:\Users\test"));
        Assert.Equal(@"C:\Users\test\", OmnibarSuggestions.AcceptedFolderText(@"C:\Users\test\"));
        Assert.Equal("/srv/www/", OmnibarSuggestions.AcceptedFolderText("/srv/www"));
    }

    [Fact]
    public void DownAndUp_StepThroughTheListAndBackToTheTypedText()
    {
        // No line is chosen at first: Enter runs what is typed.
        Assert.Equal(0, OmnibarSuggestions.MoveSelection(-1, 3, 1));
        Assert.Equal(1, OmnibarSuggestions.MoveSelection(0, 3, 1));
        Assert.Equal(-1, OmnibarSuggestions.MoveSelection(2, 3, 1));
        Assert.Equal(2, OmnibarSuggestions.MoveSelection(-1, 3, -1));
        Assert.Equal(-1, OmnibarSuggestions.MoveSelection(0, 3, -1));
        Assert.Equal(-1, OmnibarSuggestions.MoveSelection(0, 0, 1));
        // A selection left over from a longer list.
        Assert.Equal(-1, OmnibarSuggestions.MoveSelection(9, 3, 1));
    }

    [Fact]
    public void TheRenameBox_SitsOverTheNameWhereverTheViewPutsIt()
    {
        // A row of the Details or List view: the name is on the middle line.
        Assert.False(RenameBoxLayout.NameIsBelowIcon(nameTop: 6, nameHeight: 18, rowHeight: 30));
        // A tile: the name is under the icon.
        Assert.True(RenameBoxLayout.NameIsBelowIcon(nameTop: 70, nameHeight: 34, rowHeight: 110));
        Assert.False(RenameBoxLayout.NameIsBelowIcon(nameTop: 70, nameHeight: 34, rowHeight: 0));

        // The box's edge goes a little above the name, so its text lands on the name; never above the row.
        Assert.Equal(6, RenameBoxLayout.Top(nameTop: 9));
        Assert.Equal(0, RenameBoxLayout.Top(nameTop: 1));

        Assert.Equal(RenameBoxLayout.MinWidth, RenameBoxLayout.RowWidth(nameWidth: 40, available: 600));
        Assert.Equal(324, RenameBoxLayout.RowWidth(nameWidth: 300, available: 600));
        Assert.Equal(200, RenameBoxLayout.RowWidth(nameWidth: 300, available: 200));
        Assert.Equal(80, RenameBoxLayout.RowWidth(nameWidth: 300, available: 20));
    }
}
