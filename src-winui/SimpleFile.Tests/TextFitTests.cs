using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public sealed class TextFitTests
{
    // The app measures rendered width; a character budget stands in for it here.
    private static Func<string, bool> Chars(int budget) => text => text.Length <= budget;

    [Fact]
    public void TextThatFitsIsNeverChanged()
    {
        Assert.Equal("Movie Night", TextFit.End("Movie Night", Chars(11)));
        Assert.Equal("Movie Night.mkv", TextFit.FileName("Movie Night.mkv", Chars(15)));
        Assert.Equal(@"D:\Movies\2024\", TextFit.FolderPath(@"D:\Movies\2024\", Chars(15)));
    }

    [Fact]
    public void End_CutsAtTheEndAndLeavesNoSpaceBeforeTheMark()
    {
        Assert.Equal("Movie Night…", TextFit.End("Movie Night Recording", Chars(12)));
        // "Movie N…" would fit seven characters too, but "Movie …" must not appear.
        Assert.Equal("Movie…", TextFit.End("Movie Night Recording", Chars(7)));
    }

    [Fact]
    public void End_ReturnsTheMarkAloneWhenNothingFits()
    {
        Assert.Equal("…", TextFit.End("abc", Chars(0)));
    }

    [Fact]
    public void End_NeverSplitsACharacterMadeOfTwoCodeUnits()
    {
        // Each emoji is two UTF-16 code units; a cut between them would leave half a character.
        var text = "ab\U0001F600\U0001F600\U0001F600";
        var fitted = TextFit.End(text, Chars(5));
        Assert.Equal("ab\U0001F600…", fitted);
        Assert.False(char.IsHighSurrogate(fitted[^2]));
    }

    [Fact]
    public void FileName_KeepsTheStartAndTheEndWithItsExtension()
    {
        Assert.Equal("IMG_2025011…6789.jpg", TextFit.FileName("IMG_20250114_123456789.jpg", Chars(20)));
        Assert.Equal("My holiday fi…summer.mkv", TextFit.FileName("My holiday film from the summer.mkv", Chars(24)));
    }

    [Fact]
    public void FileName_ShrinksTheStartThenTheEndAndGivesUpTheExtensionLast()
    {
        const string name = "My holiday film from the summer.mkv";
        Assert.Equal("M…summer.mkv", TextFit.FileName(name, Chars(12)));
        // No leading character fits beside "summer.mkv": the end shrinks to the extension alone.
        Assert.Equal("My….mkv", TextFit.FileName(name, Chars(8)));
        Assert.Equal("M….mkv", TextFit.FileName(name, Chars(6)));
        // Not even one character fits beside the extension: cut at the end instead.
        Assert.Equal("My h…", TextFit.FileName(name, Chars(5)));
    }

    [Fact]
    public void FileName_KeepsTheWholeExtensionOfAShortName()
    {
        // The extension is more than a third of the name; the kept end must still not cut into it.
        Assert.Equal("Re….docx", TextFit.FileName("Report.docx", Chars(8)));
        Assert.Equal("R….docx", TextFit.FileName("Report.docx", Chars(7)));
        Assert.Equal("archi…r.gz", TextFit.FileName("archive.tar.gz", Chars(10)));
    }

    [Fact]
    public void FileName_TreatsOnlyAShortLastPartWithoutSpacesAsAnExtension()
    {
        Assert.Equal("READ…NG_NAME", TextFit.FileName("README_WITH_A_LONG_NAME", Chars(12)));
        // A full stop in a title, a leading dot and a very long last part are not extensions.
        Assert.Equal("Dr. S…long title", TextFit.FileName("Dr. Strange and the long title", Chars(16)));
        Assert.Equal(".git…long-name", TextFit.FileName(".gitignore-with-a-long-name", Chars(14)));
        Assert.Equal("downloa…-partial", TextFit.FileName("download.sumafile-partial", Chars(16)));
    }

    [Fact]
    public void FolderPath_DropsFoldersFromTheMiddleAndKeepsTheRootAndTheLastOnes()
    {
        const string path = @"D:\Movies\2024\A very long folder name\Another long folder\";
        Assert.Equal(@"D:\…\A very long folder name\Another long folder\", TextFit.FolderPath(path, Chars(50)));
        Assert.Equal(@"D:\…\Another long folder\", TextFit.FolderPath(path, Chars(30)));
        Assert.Equal(@"D:\…\Another long folder\", TextFit.FolderPath(path, Chars(25)));
    }

    [Fact]
    public void FolderPath_GivesUpTheRootThenCutsTheLastFolder()
    {
        const string path = @"D:\Movies\2024\A very long folder name\Another long folder\";
        Assert.Equal(@"…\Another long folder\", TextFit.FolderPath(path, Chars(22)));
        Assert.Equal(@"…\Another l…", TextFit.FolderPath(path, Chars(12)));
    }

    [Fact]
    public void FolderPath_KeepsAShareAsTheRoot()
    {
        const string path = @"\\nas\media\Movies\2024\Action\";
        Assert.Equal(@"\\nas\media\…\2024\Action\", TextFit.FolderPath(path, Chars(26)));
        Assert.Equal(@"\\nas\media\…\Action\", TextFit.FolderPath(path, Chars(22)));
        Assert.Equal(@"…\Action\", TextFit.FolderPath(path, Chars(12)));
    }

    [Fact]
    public void FolderPath_HandlesForwardSlashesRelativePathsAndASingleFolder()
    {
        Assert.Equal("/…/videos/2024/clips/", TextFit.FolderPath("/home/user/videos/2024/clips/", Chars(24)));
        Assert.Equal(@"…\Action\", TextFit.FolderPath(@"Movies\2024\Action\", Chars(12)));
        // Nothing to drop from the middle of a single folder.
        Assert.Equal(@"D:\A single ve…", TextFit.FolderPath(@"D:\A single very long folder\", Chars(15)));
    }

    [Fact]
    public void FolderPath_WithoutATrailingSeparatorStaysWithoutOne()
    {
        Assert.Equal(@"D:\…\Action", TextFit.FolderPath(@"D:\Movies\2024\Action", Chars(12)));
    }
}
