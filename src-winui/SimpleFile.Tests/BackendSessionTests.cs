using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class BackendSessionTests
{
    [Fact]
    public void JobObject_KillsServiceOnCloseWithoutTakingOpenedDocuments()
    {
        Assert.Equal(
            JobObject.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JobObject.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK,
            JobObject.DefaultLimitFlags);

        using var job = JobObject.TryCreate();
        Assert.NotNull(job);
        var flags = job.TryGetLimitFlags();
        Assert.NotNull(flags);
        Assert.Equal(
            JobObject.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            flags.Value & JobObject.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE);
        Assert.Equal(
            JobObject.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK,
            flags.Value & JobObject.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK);
    }

    [Fact]
    public async Task StartAsync_CallsHealthAndVersion_WhenServiceIsBuilt()
    {
        if (ServiceLocator.FindServiceExecutable() is null)
        {
            return;
        }

        await using var session = new BackendSession();
        await session.StartAsync();
        Assert.True(session.Health?.Ok);
        Assert.False(string.IsNullOrWhiteSpace(session.AppVersion));
        Assert.Equal("com.simplefile.desktop", session.Handshake?.Identifier);
        Assert.False(string.IsNullOrWhiteSpace(session.HomeDir));
        Assert.NotNull(session.Drives);
        Assert.True(session.Client?.IsConnected);
    }

    [Fact]
    public async Task TypedMvpMethods_WorkAgainstBuiltService()
    {
        if (ServiceLocator.FindServiceExecutable() is null)
        {
            return;
        }

        await using var session = new BackendSession();
        await session.StartAsync();

        var home = await session.GetHomeDirAsync();
        Assert.False(string.IsNullOrWhiteSpace(home));

        var drives = await session.ListDrivesAsync();
        Assert.NotEmpty(drives);

        var chunks = new List<DirectoryListingChunk>();
        var listing = await session.ListDirectoryAsync(Path.GetTempPath(), chunks.Add);
        Assert.False(string.IsNullOrWhiteSpace(listing.Path));
        Assert.NotNull(listing.Entries);
        Assert.Contains(chunks, chunk => chunk.Done);

        await session.ShowMainWindowAsync();

        var hostOwned = await Assert.ThrowsAsync<IpcException>(() => session.SelectDirectoryAsync());
        Assert.True(hostOwned.IsHostOwned);
        Assert.Equal(Protocol.ErrHostOwned, hostOwned.Code);
        Assert.StartsWith(Protocol.PrefixHostOwned, hostOwned.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReconnectAsync_RestartsServiceAfterCrash()
    {
        if (ServiceLocator.FindServiceExecutable() is null)
        {
            return;
        }

        await using var session = new BackendSession();
        await session.StartAsync();
        var firstPipe = session.PipeName;
        var home = await session.GetHomeDirAsync();

        session.KillServiceForTests();
        await WaitUntilAsync(() => session.Client is not { IsConnected: true }, TimeSpan.FromSeconds(5));

        await session.ReconnectAsync();
        Assert.NotEqual(firstPipe, session.PipeName);
        Assert.Equal(1, session.ReconnectCount);
        Assert.True(session.Client?.IsConnected);
        Assert.Equal(home, await session.GetHomeDirAsync());
        Assert.True((await session.HealthAsync()).Ok);
    }

    [Fact]
    public async Task UseClient_ReconnectsOnDemandAfterCrash()
    {
        if (ServiceLocator.FindServiceExecutable() is null)
        {
            return;
        }

        await using var session = new BackendSession();
        await session.StartAsync();
        session.KillServiceForTests();
        await WaitUntilAsync(() => session.Client is not { IsConnected: true }, TimeSpan.FromSeconds(5));

        var home = await session.GetHomeDirAsync();
        Assert.False(string.IsNullOrWhiteSpace(home));
        Assert.True(session.ReconnectCount >= 1);
        Assert.True(session.Client?.IsConnected);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(condition(), "Timed out waiting for IPC disconnect.");
    }
}
