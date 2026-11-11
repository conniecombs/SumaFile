using System.Collections.Generic;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class PluginMenuWhenTests
{
    private static ContextMenuRequest Request(int selectionCount, bool isDirectory = false) => new()
    {
        SelectionCount = selectionCount,
        SelectedExtension = "txt",
        SelectedIsDirectory = isDirectory,
    };

    [Theory]
    [InlineData("", 0, true)]
    [InlineData("selectionCount >= 1", 1, true)]
    [InlineData("selectionCount == 2 && singleFile", 2, false)]
    [InlineData("!singleFolder || selectionCount > 5", 1, true)]
    [InlineData("hasExtension(.TXT)", 1, true)]
    [InlineData("hasExtension(txt)", 1, true)]
    [InlineData("hasExtension(.md)", 1, false)]
    [InlineData("selectionCount >", 1, false)]
    [InlineData("(selectionCount == 1 || selectionCount == 2) && singleFile", 1, true)]
    [InlineData("bogus", 1, false)]
    [InlineData("selectionCount == 1 )", 1, false)]
    public void Evaluate_Cases(string when, int selectionCount, bool expected)
    {
        Assert.Equal(expected, PluginMenuWhen.Evaluate(when, "com.a", Request(selectionCount)));
    }

    [Fact]
    public void Evaluate_SingleFolder_RequiresDirectory()
    {
        Assert.True(PluginMenuWhen.Evaluate("singleFolder", "com.a", Request(1, isDirectory: true)));
        Assert.False(PluginMenuWhen.Evaluate("singleFile", "com.a", Request(1, isDirectory: true)));
    }

    [Fact]
    public void Evaluate_FolderFlag_UsesPluginPrefix()
    {
        var request = new ContextMenuRequest { FolderFlags = new HashSet<string> { "com.a:inRepository" } };
        Assert.True(PluginMenuWhen.Evaluate("folderFlag(inRepository)", "com.a", request));
        Assert.False(PluginMenuWhen.Evaluate("folderFlag(inRepository)", "com.b", request));
    }

    [Fact]
    public void Evaluate_HasDecoration_UsesPluginId()
    {
        var request = new ContextMenuRequest { SelectionDecorationPlugins = new HashSet<string> { "com.a" } };
        Assert.True(PluginMenuWhen.Evaluate("hasDecoration", "com.a", request));
        Assert.False(PluginMenuWhen.Evaluate("hasDecoration", "com.b", request));
    }

    [Fact]
    public void IsValid_ReportsErrors()
    {
        Assert.True(PluginMenuWhen.IsValid("selectionCount > 1 && hasDecoration", out var ok));
        Assert.Equal("", ok);
        Assert.False(PluginMenuWhen.IsValid("selectionCount >", out var error));
        Assert.NotEqual("", error);
        Assert.False(PluginMenuWhen.IsValid("a ? b", out _));
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void NotChain_IsBoundedInDepth(int depth, bool valid)
    {
        var expression = new string('!', depth) + "singleFile";
        Assert.Equal(valid, PluginMenuWhen.IsValid(expression, out var error));
        if (!valid)
        {
            Assert.Contains("nesting", error);
            Assert.False(PluginMenuWhen.Evaluate(expression, "com.a", Request(1)));
        }
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void ParenChain_IsBoundedInDepth(int depth, bool valid)
    {
        var expression = new string('(', depth) + "singleFile" + new string(')', depth);
        Assert.Equal(valid, PluginMenuWhen.IsValid(expression, out var error));
        if (!valid)
        {
            Assert.Contains("nesting", error);
            Assert.False(PluginMenuWhen.Evaluate(expression, "com.a", Request(1)));
        }
    }

    [Fact]
    public void HugeChains_DoNotOverflowTheStack()
    {
        Assert.False(PluginMenuWhen.IsValid(new string('!', 200000) + "singleFile", out _));
        Assert.False(PluginMenuWhen.IsValid(new string('(', 200000), out _));
    }
}
