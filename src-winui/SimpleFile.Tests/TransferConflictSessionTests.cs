using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class TransferConflictSessionTests
{
    [Fact]
    public void Remember_ApplyToAllSticksForLaterConflicts()
    {
        var session = new TransferConflictSession();
        Assert.False(session.TryGetSticky(out _));

        session.Remember("replace", applyToAll: false);
        Assert.False(session.TryGetSticky(out _));

        session.Remember("skip", applyToAll: true);
        Assert.True(session.TryGetSticky(out var sticky));
        Assert.Equal("skip", sticky);
    }
}
