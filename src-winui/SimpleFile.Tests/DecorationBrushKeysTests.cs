using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class DecorationBrushKeysTests
{
    [Theory]
    [InlineData("SfDecorationAddedBrush", "SfDecorationAddedBrush")]
    [InlineData("SfDecorationModifiedBrush", "SfDecorationModifiedBrush")]
    [InlineData("SfDecorationUntrackedBrush", "SfDecorationUntrackedBrush")]
    [InlineData("SfDecorationDeletedBrush", "SfDecorationDeletedBrush")]
    [InlineData("SfTextPrimaryBrush", "SfTextPrimaryBrush")]
    [InlineData("SfTextMutedBrush", "SfTextMutedBrush")]
    [InlineData("SfAccentBrush", "SfAccentBrush")]
    [InlineData(null, "SfTextMutedBrush")]
    [InlineData("", "SfTextMutedBrush")]
    [InlineData("   ", "SfTextMutedBrush")]
    [InlineData("SfGitAddedBrush", "SfTextMutedBrush")]
    [InlineData("sfaccentbrush", "SfTextMutedBrush")]
    [InlineData("SystemControlForegroundBaseHighBrush", "SfTextMutedBrush")]
    [InlineData("{ThemeResource SfAccentBrush}", "SfTextMutedBrush")]
    public void Normalize_KnownKeysPass_UnknownKeysFallBackToMuted(string? key, string expected)
    {
        Assert.Equal(expected, DecorationBrushKeys.Normalize(key));
    }

    [Fact]
    public void Allowed_IsExactlyTheStableDecorationAndTextKeys()
    {
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "SfDecorationAddedBrush",
                "SfDecorationModifiedBrush",
                "SfDecorationUntrackedBrush",
                "SfDecorationDeletedBrush",
                "SfTextPrimaryBrush",
                "SfTextMutedBrush",
                "SfAccentBrush",
            },
            DecorationBrushKeys.Allowed.ToHashSet(StringComparer.Ordinal));
    }
}
