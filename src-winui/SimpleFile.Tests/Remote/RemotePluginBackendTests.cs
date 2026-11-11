using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SimpleFile.PluginHost;
using SumaFile.Remote.Model;
using Xunit;

namespace SimpleFile.Tests.Remote;

/// <summary>
/// The real host drives the real remote backend: the staged <c>com.sumafile.remote</c> folder, trusted through its
/// <c>bundled.json</c>, launched by the real <see cref="PluginManager"/> and its <c>ProcessLauncher</c>, talking to the
/// loopback SFTP test server. The harness sets the process-wide <c>SIMPLEFILE_METADATA_DB</c>, so these tests run alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RemotePluginBackendCollection
{
    public const string Name = "Remote plugin backend";
}

[Collection(RemotePluginBackendCollection.Name)]
public class RemotePluginBackendTests
{
    private const int Mib = 1024 * 1024;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SavesListsConnectsAndListsTheTestServer()
    {
        await using var harness = await RemoteBackendHarness.StartAsync();

        var saved = await harness.Client.SaveProfileAsync(harness.SftpProfile("Test server"), null, CancellationToken.None);

        var profile = Assert.Single(await harness.Client.ListProfilesAsync(CancellationToken.None));
        Assert.Equal(saved.Id, profile.Id);
        Assert.Equal("Test server", profile.Name);
        Assert.Equal("sftp", profile.Protocol);
        Assert.Equal("127.0.0.1", profile.Host);
        Assert.Equal(harness.Server.Port, profile.Port);
        Assert.Equal(harness.Server.User, profile.Username);
        Assert.Equal(harness.Server.Fingerprint, profile.TrustedHostFingerprint);
        // The wire shape itself carries no secret, under any name.
        var raw = await harness.Host.InvokeBackendAsync<JsonElement>(RemoteMethods.ListProfiles, null, CancellationToken.None);
        var fields = Assert.Single(raw.EnumerateArray()).EnumerateObject().Select(p => p.Name).ToList();
        Assert.DoesNotContain(fields, name => name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(raw.GetRawText(), harness.Server.Password, StringComparison.Ordinal);

        var session = await harness.Client.ConnectAsync(saved.Id, harness.Server.Password, CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(session.SessionId));
        Assert.Equal(saved.Id, session.ProfileId);

        var listing = await harness.Client.ListDirectoryAsync(session.SessionId, "/", CancellationToken.None);
        Assert.Equal(
            RemoteBackendHarness.ServerFixtureNames.Order(StringComparer.Ordinal),
            listing.Entries.Select(e => e.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task UploadAndDownloadAppearAsProgressRowsAndComplete()
    {
        await using var harness = await RemoteBackendHarness.StartAsync();
        var session = await harness.SaveAndConnectAsync();
        const string name = "café 資料.bin";
        var payload = Pattern(2 * Mib);
        var uploadFolder = Directory.CreateDirectory(Path.Combine(harness.LocalFolder, "upload")).FullName;
        var source = Path.Combine(uploadFolder, name);
        File.WriteAllBytes(source, payload);

        var upload = await harness.Client.TransferAsync(
            Request(session, "upload", [source], "/", "error", "op-upload"), null, null, CancellationToken.None);

        AssertSucceeded(upload);
        var uploadRow = Assert.Single(harness.Rows.All);
        Assert.Equal("Uploading 1 item to 127.0.0.1:/", uploadRow.Title);
        await uploadRow.Ended.WaitAsync(Wait);
        Assert.True(uploadRow.Completed, uploadRow.Describe());
        Assert.NotEmpty(uploadRow.Reports);
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(harness.ServerRoot, name)));

        var downloadFolder = Directory.CreateDirectory(Path.Combine(harness.LocalFolder, "download")).FullName;
        var download = await harness.Client.TransferAsync(
            Request(session, "download", ["/" + name], downloadFolder, "error", "op-download"), null, null, CancellationToken.None);

        AssertSucceeded(download);
        Assert.Equal(2, harness.Rows.All.Count);
        var downloadRow = harness.Rows.All[1];
        Assert.Equal("Downloading 1 item from 127.0.0.1", downloadRow.Title);
        await downloadRow.Ended.WaitAsync(Wait);
        Assert.True(downloadRow.Completed, downloadRow.Describe());
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(downloadFolder, name)));
    }

    [Fact]
    public async Task CancelFromTheProgressRow_StopsTheDownloadAndKeepsTheExistingFile()
    {
        await using var harness = await RemoteBackendHarness.StartAsync();
        // 64 MiB at 1 MiB/s: the download is still running when the cancel lands, however fast the loopback is.
        harness.Settings.Values[RemoteBackendHarness.SettingKey("bandwidthMib")] = "1";
        var session = await harness.SaveAndConnectAsync();
        File.WriteAllBytes(Path.Combine(harness.ServerRoot, "large.bin"), Pattern(64 * Mib));
        var existing = "the existing local file"u8.ToArray();
        var local = Path.Combine(harness.LocalFolder, "large.bin");
        File.WriteAllBytes(local, existing);

        var transfer = harness.Client.TransferAsync(
            Request(session, "download", ["/large.bin"], harness.LocalFolder, "replace", "op-row-cancel"),
            null, null, CancellationToken.None);
        var row = await harness.Rows.WaitForRowAsync(0, Wait);
        await row.FirstReport.WaitAsync(Wait);
        // The Transfers row's Cancel: the host sends the row's id, not the request's.
        row.Cancel();

        var outcome = await transfer.WaitAsync(Wait);
        Assert.True(outcome.Cancelled);
        await row.Ended.WaitAsync(Wait);
        Assert.Equal("Transfer cancelled", row.FailMessage);
        Assert.Equal(existing, File.ReadAllBytes(local));
        Assert.Equal(["large.bin"], Directory.GetFileSystemEntries(harness.LocalFolder).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(harness.LocalFolder, "*.sumafile-partial*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TransferHonoursThePluginBandwidthSetting()
    {
        await using var harness = await RemoteBackendHarness.StartAsync();
        var session = await harness.SaveAndConnectAsync();
        var payload = Pattern(4 * Mib);
        var throttledSource = Path.Combine(harness.LocalFolder, "throttled.bin");
        var fastSource = Path.Combine(harness.LocalFolder, "fast.bin");
        File.WriteAllBytes(throttledSource, payload);
        File.WriteAllBytes(fastSource, payload);

        harness.Settings.Values[RemoteBackendHarness.SettingKey("bandwidthMib")] = "1";
        var throttled = Stopwatch.StartNew();
        AssertSucceeded(await harness.Client.TransferAsync(
            Request(session, "upload", [throttledSource], "/", "error", "op-throttled"), null, null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(60)));
        throttled.Stop();

        harness.Settings.Values[RemoteBackendHarness.SettingKey("bandwidthMib")] = "0";
        var fast = Stopwatch.StartNew();
        AssertSucceeded(await harness.Client.TransferAsync(
            Request(session, "upload", [fastSource], "/", "error", "op-fast"), null, null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(60)));
        fast.Stop();

        Assert.True(throttled.Elapsed >= TimeSpan.FromSeconds(3), $"The 1 MB/s upload took {throttled.Elapsed}.");
        Assert.True(fast.Elapsed < TimeSpan.FromSeconds(3), $"The unlimited upload took {fast.Elapsed}.");
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(harness.ServerRoot, "throttled.bin")));
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(harness.ServerRoot, "fast.bin")));
    }

    [Fact]
    public async Task ProfilesChangedReachesTheClientAfterASave()
    {
        await using var harness = await RemoteBackendHarness.StartAsync();
        // The first call starts the backend (and runs its legacy import against none.db, which imports nothing).
        Assert.Empty(await harness.Client.ListProfilesAsync(CancellationToken.None));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = harness.Client.OnProfilesChanged(() => changed.TrySetResult());

        await harness.Client.SaveProfileAsync(harness.SftpProfile("Announced"), null, CancellationToken.None);

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Disable_WhileATransferRuns_StopsTheBackendAndEndsTheRow()
    {
        await using var harness = await RemoteBackendHarness.StartAsync();
        harness.Settings.Values[RemoteBackendHarness.SettingKey("bandwidthMib")] = "1";
        var session = await harness.SaveAndConnectAsync();
        File.WriteAllBytes(Path.Combine(harness.ServerRoot, "slow.bin"), Pattern(64 * Mib));
        var transfer = harness.Client.TransferAsync(
            Request(session, "download", ["/slow.bin"], harness.LocalFolder, "replace", "op-disable"),
            null, null, CancellationToken.None);
        var row = await harness.Rows.WaitForRowAsync(0, Wait);
        await row.FirstReport.WaitAsync(Wait);
        var backend = Assert.Single(harness.BackendProcesses);

        await harness.Manager.DisableAsync(RemoteBackendHarness.PluginId, CancellationToken.None).WaitAsync(Wait);

        Assert.True(await harness.WaitForExitAsync(backend, TimeSpan.FromSeconds(5)), "The backend process is still running.");
        Assert.Equal(PluginState.Disabled, Assert.Single(harness.Manager.Plugins).State);
        await row.Ended.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Plugin backend stopped.", row.FailMessage);
        var failure = await Record.ExceptionAsync(() => transfer.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(failure);
        Assert.IsNotType<TimeoutException>(failure);
    }

    [Fact]
    public async Task CloseSession_DuringAThrottledDownload_DisconnectsOnceTheTransferStops()
    {
        await using var harness = await RemoteBackendHarness.StartAsync();
        // 64 MiB at 1 MiB/s: the download is still running when the window closes.
        harness.Settings.Values[RemoteBackendHarness.SettingKey("bandwidthMib")] = "1";
        File.WriteAllBytes(Path.Combine(harness.ServerRoot, "large.bin"), Pattern(64 * Mib));
        await harness.Client.SaveProfileAsync(harness.SftpProfile("Test server"), null, CancellationToken.None);
        var viewModel = new RemoteManagerViewModel(harness.Client, new LocalFolderSource());
        await viewModel.LoadProfilesAsync();
        var connected = await viewModel.ConnectSelectedProfileAsync(harness.Server.Password);
        Assert.True(connected.Connected, connected.Error);
        var session = Assert.IsType<RemoteSession>(viewModel.CurrentSession);

        var download = viewModel.DownloadRemoteEntriesAsync(
            [new RemoteFileEntry { Name = "large.bin", Path = "/large.bin" }], harness.LocalFolder);
        var row = await harness.Rows.WaitForRowAsync(0, Wait);
        await row.FirstReport.WaitAsync(Wait);
        Assert.False(download.IsCompleted);

        var closed = await viewModel.CloseSessionAsync(TimeSpan.FromSeconds(15)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(closed);
        await download.WaitAsync(Wait);
        Assert.Null(viewModel.CurrentSession);
        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => harness.Client.ListDirectoryAsync(session.SessionId, "/", CancellationToken.None).WaitAsync(Wait));
        Assert.StartsWith("remote session not found: ", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpgradedProfile_IsImportedAndConnectsThroughTheRealHost()
    {
        await using var harness = await RemoteBackendHarness.StartAsync(legacyDatabase: true);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = harness.Client.OnProfilesChanged(() => changed.TrySetResult());

        var profiles = await harness.Client.ListProfilesAsync(CancellationToken.None);

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var profile = Assert.Single(profiles);
        Assert.NotNull(harness.Server.LegacyProfileId);
        Assert.Equal(harness.Server.LegacyProfileId, profile.Id);
        Assert.Equal("Legacy SFTP", profile.Name);
        Assert.Equal("SumaFile.Remote.legacy-sftp", profile.CredentialTarget);
        Assert.Equal(harness.Server.Fingerprint, profile.TrustedHostFingerprint);

        // No host-key error: the trusted fingerprint came along with the row.
        var session = await harness.Client.ConnectAsync(profile.Id, harness.Server.Password, CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(session.SessionId));
        Assert.Equal(profile.Id, session.ProfileId);
    }

    private static RemoteTransferRequest Request(
        RemoteSession session, string direction, string[] sources, string destination, string conflict, string operationId) =>
        new()
        {
            RemoteSessionId = session.SessionId,
            Direction = direction,
            Sources = sources,
            Destination = destination,
            ConflictAction = conflict,
            OperationId = operationId,
        };

    private static void AssertSucceeded(RemoteTransferOutcome outcome)
    {
        Assert.False(outcome.Cancelled);
        Assert.Empty(outcome.RetrySources);
        Assert.All(outcome.Items, item => Assert.Null(item.Error));
    }

    /// <summary>Bytes that differ at every offset a block boundary could fall on.</summary>
    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31 + i / 251) & 0xFF);
        }

        return bytes;
    }
}
