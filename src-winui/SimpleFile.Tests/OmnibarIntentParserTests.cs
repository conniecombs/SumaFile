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
    public void FilterCommands_ListsNoCoreGitCommands()
    {
        // Git commands come from the Git plugin (plugin:com.sumafile.git:*) and stay listed while it is turned off.
        Assert.DoesNotContain(OmnibarIntentParser.FilterCommands("git"), command => command.Id.StartsWith("git-", StringComparison.Ordinal));
        Assert.Contains(OmnibarIntentParser.FilterCommands("settings"), command => command.Id == "settings");
    }

    [Fact]
    public void Parse_ResolvesRelativeFolderAgainstCurrentPath()
    {
        var full = System.IO.Path.Combine("/home/u", "Docs");
        var intent = OmnibarIntentParser.Parse("Docs", OmnibarMode.Navigate, "/home/u", p => p == full);

        Assert.Equal(OmnibarIntentKind.Navigate, intent.Kind);
        Assert.Equal(full, intent.Value);
        Assert.Null(intent.StatusMessage);
    }

    [Fact]
    public void Parse_FallsBackToFilterWithMessageWhenRelativeFolderMissing()
    {
        var intent = OmnibarIntentParser.Parse("zzz", OmnibarMode.Navigate, @"C:\Users", _ => false);

        Assert.Equal(OmnibarIntentKind.Filter, intent.Kind);
        Assert.Equal("zzz", intent.Value);
        Assert.Equal("No folder named 'zzz' here. Filtering instead.", intent.StatusMessage);
    }

    [Fact]
    public void Parse_WithoutDirectoryExistsKeepsRawNavigate()
    {
        var intent = OmnibarIntentParser.Parse("zzz", OmnibarMode.Navigate, @"C:\Users");

        Assert.Equal(OmnibarIntentKind.Navigate, intent.Kind);
        Assert.Equal("zzz", intent.Value);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(@"C:\Users")]
    [InlineData(@"\\server\share")]
    [InlineData("./x")]
    [InlineData("/x")]
    public void Parse_RootedOrDotTokensNavigateWithoutExistenceCheck(string text)
    {
        var intent = OmnibarIntentParser.Parse(text, OmnibarMode.Navigate, @"C:\Users", _ => false);

        Assert.Equal(OmnibarIntentKind.Navigate, intent.Kind);
        Assert.Equal(text, intent.Value);
    }

    [Fact]
    public void Parse_DotPrefixedNamesStillCheckForAFolder()
    {
        var intent = OmnibarIntentParser.Parse(".git", OmnibarMode.Navigate, @"C:\Users", _ => false);

        Assert.Equal(OmnibarIntentKind.Filter, intent.Kind);
        Assert.Equal(".git", intent.Value);
    }

    [Theory]
    [InlineData("C:", @"C:\")]
    [InlineData("c:", @"C:\")]
    public void Parse_BareDriveLetterNormalizesToRoot(string text, string expected)
    {
        var intent = OmnibarIntentParser.Parse(text, OmnibarMode.Navigate, @"D:\x", _ => false);

        Assert.Equal(OmnibarIntentKind.Navigate, intent.Kind);
        Assert.Equal(expected, intent.Value);
    }
}
