using System.Text.Json;
using IpcJson = SimpleFile.Ipc.IpcJson;
using SumaFile.Remote.Model;
using Xunit;

namespace SimpleFile.Tests.Remote;

public class RemoteBackendClientTests
{
    private const string ProfileJson = """
        {"id":"prod","name":"Production","protocol":"sftp","host":"example.com","port":22,"username":"deploy",
         "root_path":"/var/www","auth_kind":"password","insecure_plain_ftp":false,"passive_mode":true,
         "credential_target":"SumaFile.Remote.prod","private_key_path":"C:\\keys\\id_ed25519",
         "trusted_host_fingerprint":"SHA256:abc"}
        """;

    private const string EntryJson = """
        {"name":"café 資料","path":"/café 資料","is_dir":true,"is_symlink":false,"size":0,
         "modified":"2026-09-21T00:00:00Z","extension":""}
        """;

    private const string OutcomeJson = """
        {"items":[{"source":"C:\\a.bin","destination":"/a.bin","is_dir":false,"status":"cancelled","bytes":7}],
         "retry_sources":["C:\\a.bin"],"cancelled":true}
        """;

    [Fact]
    public async Task ClientMethods_UseTheProtocolNamesAndParamShapes()
    {
        var host = new FakePluginHost();
        host.Respond(RemoteMethods.ListProfiles, "[]");
        host.Respond(RemoteMethods.SaveProfile, ProfileJson);
        host.Respond(RemoteMethods.TestProfile, """{"ok":true,"message":"fine","capabilities":["sftp"]}""");
        host.Respond(RemoteMethods.Connect, """{"session_id":"s1","profile_id":"prod","protocol":"sftp","root_path":"/"}""");
        host.Respond(RemoteMethods.ListDirectory, $$"""{"path":"/","parent":null,"entries":[{{EntryJson}}],"is_network":true}""");
        host.Respond(RemoteMethods.CreateDirectory, EntryJson);
        host.Respond(RemoteMethods.Rename, EntryJson);
        host.Respond(RemoteMethods.DeleteEntries, """["/a","/b"]""");
        host.Respond(RemoteMethods.Transfer, OutcomeJson);
        var client = new RemoteBackendClient(host);
        var input = new RemoteProfileInput
        {
            Name = "Production",
            Protocol = "sftp",
            Host = "example.com",
            Port = 22,
            Username = "deploy",
            RootPath = "/var/www",
            AuthKind = "password",
        };

        await client.ListProfilesAsync(CancellationToken.None);
        await client.SaveProfileAsync(input, "pw", CancellationToken.None);
        await client.DeleteProfileAsync("prod", CancellationToken.None);
        await client.TestProfileAsync(input, "pw", CancellationToken.None);
        var session = await client.ConnectAsync("prod", "pw", CancellationToken.None);
        var listing = await client.ListDirectoryAsync("s1", "/", CancellationToken.None);
        var created = await client.CreateDirectoryAsync("s1", "/", "café 資料", CancellationToken.None);
        var renamed = await client.RenameEntryAsync("s1", "/old", "café 資料", CancellationToken.None);
        var deleted = await client.DeleteEntriesAsync("s1", ["/a", "/b"], CancellationToken.None);
        await client.DisconnectAsync("s1", CancellationToken.None);
        var outcome = await client.TransferAsync(
            new RemoteTransferRequest
            {
                RemoteSessionId = "s1",
                Direction = "upload",
                Sources = [@"C:\a.bin"],
                Destination = "/",
                ConflictAction = "replace",
                OperationId = "op-1",
            },
            null,
            null,
            CancellationToken.None);

        const string inputJson =
            """{"name":"Production","protocol":"sftp","host":"example.com","port":22,"username":"deploy","root_path":"/var/www","auth_kind":"password","insecure_plain_ftp":false,"passive_mode":true}""";
        Assert.Equal(
            [
                ("profiles.list", "null"),
                ("profiles.save", $$"""{"profile":{{inputJson}},"secret":"pw"}"""),
                ("profiles.delete", """{"profileId":"prod"}"""),
                ("profiles.test", $$"""{"profile":{{inputJson}},"secret":"pw"}"""),
                ("session.connect", """{"profileId":"prod","secret":"pw"}"""),
                ("fs.list", """{"remoteSessionId":"s1","path":"/"}"""),
                ("fs.create_directory", """{"remoteSessionId":"s1","path":"/","name":"café 資料"}"""),
                ("fs.rename", """{"remoteSessionId":"s1","path":"/old","newName":"café 資料"}"""),
                ("fs.delete", """{"remoteSessionId":"s1","paths":["/a","/b"]}"""),
                ("session.disconnect", """{"remoteSessionId":"s1"}"""),
                ("transfer.run", """{"remoteSessionId":"s1","direction":"upload","sources":["C:\\a.bin"],"destination":"/","conflictAction":"replace","operationId":"op-1"}"""),
            ],
            host.Calls.Select(call => (call.Method, Normalize(call.ArgsJson))).ToArray());

        // Every call's params are exactly the keys protocol.json documents for its method.
        using var protocol = JsonDocument.Parse(File.ReadAllText(RemoteProtocolPath()));
        var methods = protocol.RootElement.GetProperty("methods");
        Assert.Equal(11, methods.EnumerateObject().Count());
        Assert.Equal(
            methods.EnumerateObject().Select(method => method.Name).Order(StringComparer.Ordinal),
            host.Calls.Select(call => call.Method).Order(StringComparer.Ordinal));
        foreach (var call in host.Calls)
        {
            var documented = methods.GetProperty(call.Method).GetProperty("params");
            using var sent = JsonDocument.Parse(call.ArgsJson);
            if (documented.ValueKind == JsonValueKind.Null)
            {
                Assert.Equal(JsonValueKind.Null, sent.RootElement.ValueKind);
                continue;
            }

            Assert.Equal(
                documented.EnumerateObject().Select(param => param.Name).Order(StringComparer.Ordinal),
                sent.RootElement.EnumerateObject().Select(param => param.Name).Order(StringComparer.Ordinal));
        }

        // The results read their snake_case shapes.
        Assert.Equal(("s1", "prod", "sftp", "/"), (session.SessionId, session.ProfileId, session.Protocol, session.RootPath));
        var entry = Assert.Single(listing.Entries);
        Assert.Equal(("café 資料", "/café 資料", true), (entry.Name, entry.Path, entry.IsDir));
        Assert.True(listing.IsNetwork);
        Assert.Null(listing.Parent);
        Assert.Equal("café 資料", created.Name);
        Assert.Equal("/café 資料", renamed.Path);
        Assert.Equal(["/a", "/b"], deleted);
        Assert.True(outcome.Cancelled);
        Assert.Equal([@"C:\a.bin"], outcome.RetrySources);
        var item = Assert.Single(outcome.Items);
        Assert.Equal((@"C:\a.bin", "/a.bin", false, "cancelled", 7UL, (string?)null),
            (item.Source, item.Destination, item.IsDir, item.Status, item.Bytes, item.Error));
    }

    [Fact]
    public async Task ListProfiles_ReadsSnakeCaseFieldsAndCarriesNoSecret()
    {
        var host = new FakePluginHost();
        host.Respond(RemoteMethods.ListProfiles, $"[{ProfileJson}]");
        var client = new RemoteBackendClient(host);

        var profile = Assert.Single(await client.ListProfilesAsync(CancellationToken.None));

        Assert.Equal("prod", profile.Id);
        Assert.Equal("Production", profile.Name);
        Assert.Equal("sftp", profile.Protocol);
        Assert.Equal("example.com", profile.Host);
        Assert.Equal(22, profile.Port);
        Assert.Equal("deploy", profile.Username);
        Assert.Equal("/var/www", profile.RootPath);
        Assert.Equal("password", profile.AuthKind);
        Assert.False(profile.InsecurePlainFtp);
        Assert.True(profile.PassiveMode);
        Assert.Equal("SumaFile.Remote.prod", profile.CredentialTarget);
        Assert.Equal(@"C:\keys\id_ed25519", profile.PrivateKeyPath);
        Assert.Equal("SHA256:abc", profile.TrustedHostFingerprint);
        Assert.Equal("null", Assert.Single(host.Calls).ArgsJson);

        // The DTO has no place for a secret, and writes back exactly the protocol's RemoteProfile fields.
        Assert.DoesNotContain(typeof(RemoteProfile).GetProperties(), property =>
            property.Name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("password", StringComparison.OrdinalIgnoreCase));
        using var protocol = JsonDocument.Parse(File.ReadAllText(RemoteProtocolPath()));
        var documented = protocol.RootElement.GetProperty("types").GetProperty("RemoteProfile").GetProperty("fields")
            .EnumerateArray().Select(field => field.GetString()!).ToArray();
        using var written = JsonDocument.Parse(JsonSerializer.Serialize(profile, IpcJson.Options));
        Assert.Equal(documented, written.RootElement.EnumerateObject().Select(field => field.Name).ToArray());
    }

    [Fact]
    public async Task Transfer_CancelSendsOneCancelWithTheRequestIdAndReturnsTheOutcome()
    {
        var host = new FakePluginHost();
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Responders[RemoteMethods.Transfer] = _ => reply.Task;
        var client = new RemoteBackendClient(host);
        using var cancellation = new CancellationTokenSource();

        var running = client.TransferAsync(Request("op-1"), null, null, cancellation.Token);
        cancellation.Cancel();
        cancellation.Cancel();

        // The cancel goes out at once, but the call keeps waiting for the backend's outcome.
        var cancels = host.Calls.Where(call => call.Method == RemoteMethods.Cancel).ToArray();
        var cancel = Assert.Single(cancels);
        Assert.Equal("""{"operationId":"op-1"}""", cancel.ArgsJson);
        Assert.False(running.IsCompleted);

        reply.SetResult(OutcomeJson);
        var outcome = await running;

        Assert.True(outcome.Cancelled);
        Assert.Equal([@"C:\a.bin"], outcome.RetrySources);
        Assert.Single(host.Calls, call => call.Method == RemoteMethods.Cancel);
        Assert.Single(host.Calls, call => call.Method == RemoteMethods.Transfer);
    }

    [Fact]
    public async Task Transfer_ReportsOnlyItsOwnProgressAndTuningEvents()
    {
        var host = new FakePluginHost();
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Responders[RemoteMethods.Transfer] = _ => reply.Task;
        var client = new RemoteBackendClient(host);
        var progress = new Recorder<RemoteTransferProgress>();
        var tuning = new Recorder<RemoteTransferTuning>();

        var running = client.TransferAsync(Request("op-1"), progress, tuning, CancellationToken.None);
        host.Raise(RemoteEvents.TransferProgress,
            """{"operationId":"op-2","current":1,"total":2,"currentFiles":0,"totalFiles":1,"currentItem":"other","status":"running"}""");
        host.Raise(RemoteEvents.TransferProgress,
            """{"operationId":"op-1","current":5,"total":10,"currentFiles":1,"totalFiles":2,"currentItem":"café 資料.txt","status":"running"}""");
        host.Raise(RemoteEvents.TransferTuning, """{"operationId":"op-2","mode":"other","explanation":"not mine"}""");
        host.Raise(RemoteEvents.TransferTuning, """{"operationId":"op-1","mode":"sequential","explanation":"one file at a time"}""");
        reply.SetResult(OutcomeJson);
        await running;
        host.Raise(RemoteEvents.TransferProgress,
            """{"operationId":"op-1","current":10,"total":10,"currentFiles":2,"totalFiles":2,"currentItem":"late","status":"done"}""");

        var update = Assert.Single(progress.Items);
        Assert.Equal(("op-1", 5UL, 10UL, 1UL, 2UL, "café 資料.txt", "running"),
            (update.OperationId, update.Current, update.Total, update.CurrentFiles, update.TotalFiles, update.CurrentItem, update.Status));
        var tune = Assert.Single(tuning.Items);
        Assert.Equal(("op-1", "sequential", "one file at a time"), (tune.OperationId, tune.Mode, tune.Explanation));
        Assert.Equal(0, host.SubscriptionCount(RemoteEvents.TransferProgress));
        Assert.Equal(0, host.SubscriptionCount(RemoteEvents.TransferTuning));
    }

    [Fact]
    public void ProfilesChanged_InvokesTheHandlerUntilDisposed()
    {
        var host = new FakePluginHost();
        var client = new RemoteBackendClient(host);
        var calls = 0;

        var subscription = client.OnProfilesChanged(() => calls++);
        host.Raise(RemoteEvents.ProfilesChanged, "{}");
        host.Raise(RemoteEvents.ProfilesChanged, "{}");
        subscription.Dispose();
        host.Raise(RemoteEvents.ProfilesChanged, "{}");

        Assert.Equal(2, calls);
        Assert.Equal(0, host.SubscriptionCount(RemoteEvents.ProfilesChanged));
    }

    private static RemoteTransferRequest Request(string operationId) => new()
    {
        RemoteSessionId = "s1",
        Direction = "upload",
        Sources = [@"C:\a.bin"],
        Destination = "/",
        ConflictAction = "error",
        OperationId = operationId,
    };

    private static string Normalize(string json) =>
        JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement,
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string RemoteProtocolPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "plugins", "remote", "protocol.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("plugins/remote/protocol.json not found above the test output.");
    }

    /// <summary>Records synchronously, unlike <see cref="Progress{T}"/>, which posts to the thread pool.</summary>
    private sealed class Recorder<T> : IProgress<T>
    {
        private readonly List<T> _items = [];

        public IReadOnlyList<T> Items
        {
            get
            {
                lock (_items)
                {
                    return _items.ToArray();
                }
            }
        }

        public void Report(T value)
        {
            lock (_items)
            {
                _items.Add(value);
            }
        }
    }
}
