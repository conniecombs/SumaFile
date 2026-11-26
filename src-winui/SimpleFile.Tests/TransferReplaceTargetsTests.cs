using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class TransferReplaceTargetsTests
{
    [Fact]
    public void ADestinationAnsweredWithReplaceIsKnownHoweverItIsSpelled()
    {
        var targets = new TransferReplaceTargets();
        Assert.False(targets.Contains(@"C:\dest\x.txt"));

        targets.Add(@"C:\dest\x.txt");

        Assert.True(targets.Contains(@"C:\dest\x.txt"));
        Assert.True(targets.Contains(@"c:\DEST\X.TXT"));
        Assert.True(targets.Contains("C:/dest/x.txt"));
        Assert.False(targets.Contains(@"C:\dest\y.txt"));
    }

    [Fact]
    public void AFailureWithoutADestinationIsNeverAKnownTarget()
    {
        var targets = new TransferReplaceTargets();

        targets.Add("");
        targets.Add(null);

        Assert.False(targets.Contains(""));
        Assert.False(targets.Contains(null));
        Assert.False(targets.Contains("   "));
    }

    /// <summary>
    /// Two conflicts with one destination, both answered with Replace, were sent as two Replace items: the
    /// second replaced what the first had just put there, and in a move the first file was gone for good.
    /// </summary>
    [Fact]
    public void TheAppOffersReplaceOnlyOncePerDestinationInARound()
    {
        var transfer = File.ReadAllText(
            Path.Combine(UiAuditSprint3Tests.FindRepoRoot(), "SimpleFile.App", "MainWindow.Transfer.cs"));
        var start = transfer.IndexOf("private async Task<TransferItemRequest[]?> ResolveTransferFailuresAsync(", StringComparison.Ordinal);
        var end = transfer.IndexOf("private async Task<bool?> PromptRetryFailedTransfersAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var method = transfer[start..end];

        Assert.Contains("var replaced = new TransferReplaceTargets();", method);
        var allowReplace = method.IndexOf("var allowReplace =", StringComparison.Ordinal);
        var allowReplaceEnd = method.IndexOf(';', allowReplace);
        Assert.Contains("!replaced.Contains(failure.Destination)", method[allowReplace..allowReplaceEnd]);
        Assert.Contains("replaced.Add(failure.Destination);", method);
    }
}
