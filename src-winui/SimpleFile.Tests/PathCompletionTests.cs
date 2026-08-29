using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class PathCompletionTests
{
    [Fact]
    public void TrySplit_SeparatesDirectoryAndPrefix()
    {
        Assert.True(PathCompletion.TrySplit(@"C:\Users\te", out var directory, out var prefix));
        Assert.Equal(@"C:\Users", directory);
        Assert.Equal("te", prefix);

        Assert.True(PathCompletion.TrySplit(@"C:\Users\", out directory, out prefix));
        Assert.Equal(@"C:\Users", directory);
        Assert.Equal("", prefix);
    }

    [Fact]
    public void Suggest_FiltersByPrefixCaseInsensitive()
    {
        var matches = PathCompletion.Suggest(
            [@"C:\Users\Desktop", @"C:\Users\Documents", @"C:\Users\notes.txt"],
            "do");
        Assert.Equal([@"C:\Users\Documents"], matches);
    }
}
