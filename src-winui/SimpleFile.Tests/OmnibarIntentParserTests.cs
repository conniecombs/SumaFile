using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class OmnibarIntentParserTests
{
    [Theory]
    [InlineData(OmnibarMode.Navigate, @"C:\Users\Connie", OmnibarIntentKind.Navigate, @"C:\Users\Connie")]
    [InlineData(OmnibarMode.Search, "invoice", OmnibarIntentKind.Search, "invoice")]
    [InlineData(OmnibarMode.Filter, "jpg", OmnibarIntentKind.Filter, "jpg")]
    [InlineData(OmnibarMode.Command, "copy path", OmnibarIntentKind.Command, "copy path")]
    [InlineData(OmnibarMode.Navigate, ">copy path", OmnibarIntentKind.Command, "copy path")]
    [InlineData(OmnibarMode.Navigate, "?summer photos", OmnibarIntentKind.Search, "summer photos")]
    [InlineData(OmnibarMode.Navigate, "~draft", OmnibarIntentKind.Filter, "draft")]
    public void Parse_UsesModeAndPowerUserPrefixes(
        OmnibarMode mode,
        string text,
        OmnibarIntentKind expectedKind,
        string expectedValue)
    {
        var intent = OmnibarIntentParser.Parse(text, mode, @"C:\Users");

        Assert.Equal(expectedKind, intent.Kind);
        Assert.Equal(expectedValue, intent.Value);
    }

    [Fact]
    public void Parse_EmptyNavigateKeepsCurrentPath()
    {
        var intent = OmnibarIntentParser.Parse("", OmnibarMode.Navigate, @"C:\Users\test");

        Assert.Equal(OmnibarIntentKind.Navigate, intent.Kind);
        Assert.Equal(@"C:\Users\test", intent.Value);
    }

    [Fact]
    public void FilterCommands_RespectsGitAvailability()
    {
        Assert.DoesNotContain(
            OmnibarIntentParser.FilterCommands("git", includeGit: false),
            command => command.Group == "Git");
        Assert.Contains(
            OmnibarIntentParser.FilterCommands("git", includeGit: true),
            command => command.Id == "git-panel");
    }
}
