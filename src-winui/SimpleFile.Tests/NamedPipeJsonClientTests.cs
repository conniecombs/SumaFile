using System.Text.Json;
using SimpleFile.Ipc;
using Xunit;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

public class NamedPipeJsonClientTests
{
    [Fact]
    public async Task HandshakeAndHealth_RoundTrip()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var handshakeTask = client.HandshakeAsync("secret-token");
        var request = await server.ReadRequestAsync();
        Assert.Equal(Protocol.HandshakeMethod, request.Method);
        Assert.Equal(1, request.Id);
        var parameters = Assert.IsType<JsonElement>(request.Params);
        Assert.Equal("secret-token", parameters.GetProperty("authToken").GetString());
        Assert.True(parameters.GetProperty("binaryHotFrames").GetBoolean());

        await server.SendResultAsync(
            request.Id,
            new HandshakeResult
            {
                ProtocolVersion = 1,
                AppVersion = "1.0.1",
                Identifier = Protocol.Identifier,
                MethodCount = Protocol.DomainMethodCount,
            });

        var handshake = await handshakeTask;
        Assert.Equal(Protocol.Identifier, handshake.Identifier);
        Assert.Equal(Protocol.DomainMethodCount, handshake.MethodCount);

        var healthTask = client.HealthAsync();
        var healthRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.HealthMethod, healthRequest.Method);
        await server.SendResultAsync(healthRequest.Id, new HealthResult
        {
            Ok = true,
            ProtocolVersion = 1,
            AppVersion = "1.0.1",
        });

        var health = await healthTask;
        Assert.True(health.Ok);
        Assert.Equal("1.0.1", health.AppVersion);
    }

    [Fact]
    public async Task Invoke_MatchesResponsesByIdOutOfOrder()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var first = client.InvokeAsync<int>("alpha", new { });
        var firstRequest = await server.ReadRequestAsync();
        var second = client.InvokeAsync<int>("beta", new { });
        var secondRequest = await server.ReadRequestAsync();

        Assert.Equal("alpha", firstRequest.Method);
        Assert.Equal("beta", secondRequest.Method);
        Assert.Equal(2, client.InFlightCount);

        await server.SendResultAsync(secondRequest.Id, 20);
        await server.SendResultAsync(firstRequest.Id, 10);

        Assert.Equal(10, await first);
        Assert.Equal(20, await second);
        Assert.Equal(0, client.InFlightCount);
    }

    [Fact]
    public async Task ListDirectory_FiltersChunksByRequestId()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var seen = new List<DirectoryListingChunk>();
        var listingTask = client.ListDirectoryAsync(@"C:\Users\Public", seen.Add);
        var request = await server.ReadRequestAsync();
        Assert.Equal(Protocol.ListDirectoryMethod, request.Method);
        var parameters = Assert.IsType<JsonElement>(request.Params);
        Assert.Equal(@"C:\Users\Public", parameters.GetProperty("path").GetString());

        await server.SendNotificationAsync(
            Protocol.ListDirectoryChunkEvent,
            new DirectoryListingChunkNotification
            {
                RequestId = request.Id + 99,
                Path = @"C:\other",
                Entries = [],
                ChunkIndex = 0,
                Done = true,
            });
        await server.SendNotificationAsync(
            Protocol.ListDirectoryChunkEvent,
            new DirectoryListingChunkNotification
            {
                RequestId = request.Id,
                Path = @"C:\Users\Public",
                Parent = @"C:\Users",
                Entries =
                [
                    new FileEntry { Name = "notes.txt", Path = @"C:\Users\Public\notes.txt", Extension = "txt" },
                ],
                ChunkIndex = 0,
                Done = true,
            });
        await server.SendResultAsync(
            request.Id,
            new DirectoryListing
            {
                Path = @"C:\Users\Public",
                Parent = @"C:\Users",
                Entries =
                [
                    new FileEntry { Name = "notes.txt", Path = @"C:\Users\Public\notes.txt", Extension = "txt" },
                ],
            });

        var listing = await listingTask;
        Assert.Equal(@"C:\Users\Public", listing.Path);
        Assert.Single(listing.Entries);
        Assert.Single(seen);
        Assert.True(seen[0].Done);
        Assert.Equal("notes.txt", seen[0].Entries[0].Name);
    }

    [Fact]
    public async Task ListDirectory_LightStreamedRequestMergesMetadataOnlyResult()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var seen = new List<DirectoryListingChunk>();
        var listingTask = client.ListDirectoryAsync(
            @"C:\Users\Public",
            seen.Add,
            options: new ListDirectoryOptions
            {
                Mode = "light",
                FinalEntries = false,
                SortBy = "name",
                SortAscending = true,
                IncludeHidden = true,
            });
        var request = await server.ReadRequestAsync();
        var parameters = Assert.IsType<JsonElement>(request.Params);
        Assert.Equal("light", parameters.GetProperty("mode").GetString());
        Assert.False(parameters.GetProperty("finalEntries").GetBoolean());
        Assert.Equal("name", parameters.GetProperty("sortBy").GetString());
        Assert.True(parameters.GetProperty("sortAscending").GetBoolean());
        Assert.True(parameters.GetProperty("includeHidden").GetBoolean());

        await server.SendNotificationAsync(
            Protocol.ListDirectoryChunkEvent,
            new DirectoryListingChunkNotification
            {
                RequestId = request.Id,
                Path = @"C:\Users\Public",
                Parent = @"C:\Users",
                Entries =
                [
                    new FileEntry { Name = "notes.txt", Size = 42 },
                    new FileEntry { Name = "Documents", IsDir = true },
                ],
                ChunkIndex = 0,
                Done = true,
            });
        await server.SendResultAsync(
            request.Id,
            new DirectoryListing
            {
                Path = @"C:\Users\Public",
                Parent = @"C:\Users",
                Entries = [],
            });

        var listing = await listingTask;
        Assert.Equal(2, listing.Entries.Count);
        var entry = Assert.Single(listing.Entries, item => item.Name == "notes.txt");
        Assert.Equal(@"C:\Users\Public\notes.txt", entry.Path);
        Assert.Equal("txt", entry.Extension);
        var folder = Assert.Single(listing.Entries, item => item.Name == "Documents");
        Assert.True(folder.IsDir);
        Assert.Equal(@"C:\Users\Public\Documents", folder.Path);
        Assert.Equal("", folder.Extension);
        Assert.Single(seen);
        Assert.Equal(@"C:\Users\Public\notes.txt", seen[0].Entries[0].Path);
        Assert.Equal(@"C:\Users\Public\Documents", seen[0].Entries[1].Path);
    }

    [Fact]
    public async Task BinaryListDirectory_LightStreamedRequestNormalizesIconCriticalFields()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var seen = new List<DirectoryListingChunk>();
        var listingTask = client.ListDirectoryAsync(
            @"C:\Users\Public",
            seen.Add,
            options: new ListDirectoryOptions
            {
                Mode = "light",
                FinalEntries = false,
                SortBy = "name",
                SortAscending = true,
                IncludeHidden = true,
            });
        var request = await server.ReadRequestAsync();

        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeDirectoryListingChunk(
            new DirectoryListingChunkNotification
            {
                RequestId = request.Id,
                Path = @"C:\Users\Public",
                Parent = @"C:\Users",
                Entries =
                [
                    new FileEntry { Name = "notes.txt", Size = 42 },
                    new FileEntry { Name = "Documents", IsDir = true },
                ],
                ChunkIndex = 0,
                Done = true,
                IsNetwork = false,
            }));
        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeDirectoryListingResult(
            request.Id,
            new DirectoryListing
            {
                Path = @"C:\Users\Public",
                Parent = @"C:\Users",
                Entries = [],
                IsNetwork = false,
            }));

        var listing = await listingTask;
        Assert.Equal(2, listing.Entries.Count);
        var file = Assert.Single(listing.Entries, item => item.Name == "notes.txt");
        Assert.Equal(@"C:\Users\Public\notes.txt", file.Path);
        Assert.Equal("txt", file.Extension);
        var folder = Assert.Single(listing.Entries, item => item.Name == "Documents");
        Assert.True(folder.IsDir);
        Assert.Equal(@"C:\Users\Public\Documents", folder.Path);
        Assert.Equal("", folder.Extension);
        Assert.Single(seen);
        Assert.Equal(@"C:\Users\Public\notes.txt", seen[0].Entries[0].Path);
        Assert.Equal(@"C:\Users\Public\Documents", seen[0].Entries[1].Path);
    }

    [Fact]
    public async Task BinaryListDirectory_DeliversChunksAndResult()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var seen = new List<DirectoryListingChunk>();
        var listingTask = client.ListDirectoryAsync(@"C:\Users\Public", seen.Add);
        var request = await server.ReadRequestAsync();

        var entry = new FileEntry
        {
            Name = "notes.txt",
            Path = @"C:\Users\Public\notes.txt",
            Extension = "txt",
            Size = 42,
            Modified = "2026-08-25 12:00",
        };

        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeDirectoryListingChunk(
            new DirectoryListingChunkNotification
            {
                RequestId = request.Id,
                Path = @"C:\Users\Public",
                Parent = @"C:\Users",
                Entries = [entry],
                ChunkIndex = 0,
                Done = true,
                IsNetwork = false,
            }));
        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeDirectoryListingResult(
            request.Id,
            new DirectoryListing
            {
                Path = @"C:\Users\Public",
                Parent = @"C:\Users",
                Entries = [entry],
                IsNetwork = false,
            }));

        var listing = await listingTask;
        Assert.Single(seen);
        Assert.True(seen[0].Done);
        Assert.Equal(@"C:\Users\Public", listing.Path);
        Assert.Equal("notes.txt", listing.Entries[0].Name);
    }

    [Fact]
    public async Task On_DeliversNotificationsAndUnsubscribeStopsThem()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var seen = new List<string>();
        using (client.On<FileChangeEvent>(Protocol.FileChangeEvent, change => seen.Add(change.Path)))
        {
            var invoke = client.HealthAsync();
            var request = await server.ReadRequestAsync();
            await server.SendNotificationAsync(
                Protocol.FileChangeEvent,
                new FileChangeEvent { Path = @"C:\a.txt", Kind = "create" });
            await server.SendResultAsync(request.Id, new HealthResult { Ok = true, ProtocolVersion = 1 });
            await invoke;
        }

        var after = client.HealthAsync();
        var afterRequest = await server.ReadRequestAsync();
        await server.SendNotificationAsync(
            Protocol.FileChangeEvent,
            new FileChangeEvent { Path = @"C:\b.txt", Kind = "modify" });
        await server.SendResultAsync(afterRequest.Id, new HealthResult { Ok = true, ProtocolVersion = 1 });
        await after;

        Assert.Equal([@"C:\a.txt"], seen);
    }

    [Fact]
    public async Task BinaryNotifications_DeliverFileChangeAndProgress()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var changes = new List<FileChangeEvent>();
        var progress = new List<ProgressUpdate>();
        using var changeSubscription = client.On<FileChangeEvent>(Protocol.FileChangeEvent, changes.Add);
        using var progressSubscription = client.On<ProgressUpdate>(Protocol.OperationProgressEvent, progress.Add);

        var invoke = client.HealthAsync();
        var request = await server.ReadRequestAsync();
        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeFileChange(
            new FileChangeEvent { Path = @"C:\a.txt", Kind = "modify" }));
        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeProgressUpdate(
            new ProgressUpdate
            {
                OperationId = "op_1",
                OperationType = "copy",
                Current = 4,
                Total = 8,
                CurrentFiles = 1,
                TotalFiles = 2,
                CurrentItem = @"C:\a.txt",
                Status = "running",
            }));
        await server.SendResultAsync(request.Id, new HealthResult { Ok = true, ProtocolVersion = 1 });
        await invoke;

        Assert.Single(changes);
        Assert.Equal(@"C:\a.txt", changes[0].Path);
        Assert.Single(progress);
        Assert.Equal("op_1", progress[0].OperationId);
        Assert.Equal(8ul, progress[0].Total);
    }

    [Fact]
    public async Task SearchFiles_StreamsBatchesCompletionAndUnsubscribes()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var batches = new List<SearchResult[]>();
        var completions = new List<int>();
        var search = client.SearchFilesAsync(
            new SearchOptions
            {
                Query = "alpha",
                SearchPath = @"C:\Users\Public",
                SearchId = "search-test",
                MaxResults = 10,
            },
            batches.Add,
            completions.Add);

        var request = await server.ReadRequestAsync();
        Assert.Equal(Protocol.SearchFilesMethod, request.Method);
        var parameters = Assert.IsType<JsonElement>(request.Params);
        var options = parameters.GetProperty("options");
        Assert.Equal(@"C:\Users\Public", options.GetProperty("search_path").GetString());
        Assert.Equal("search-test", options.GetProperty("search_id").GetString());

        await server.SendNotificationAsync(
            Protocol.SearchResultsBatchEvent,
            new SearchResultsBatchEvent
            {
                SearchId = "search-test",
                Results =
                [
                    new SearchResult { Name = "alpha.txt", Path = @"C:\Users\Public\alpha.txt" },
                ],
            });
        await server.SendNotificationAsync(
            Protocol.SearchCompleteEvent,
            new SearchCompleteEvent { SearchId = "search-test", Count = 1 });
        await server.SendResultAsync(
            request.Id,
            new[]
            {
                new SearchResult { Name = "alpha.txt", Path = @"C:\Users\Public\alpha.txt" },
            });

        var results = await search;
        Assert.Single(results);
        Assert.Single(batches);
        Assert.Equal([1], completions);

        var after = client.HealthAsync();
        var afterRequest = await server.ReadRequestAsync();
        await server.SendNotificationAsync(
            Protocol.SearchResultsBatchEvent,
            new SearchResultsBatchEvent
            {
                SearchId = "old-search",
                Results =
                [
                    new SearchResult { Name = "beta.txt", Path = @"C:\Users\Public\beta.txt" },
                ],
            });
        await server.SendNotificationAsync(
            Protocol.SearchCompleteEvent,
            new SearchCompleteEvent { SearchId = "old-search", Count = 2 });
        await server.SendResultAsync(afterRequest.Id, new HealthResult { Ok = true, ProtocolVersion = 1 });
        await after;

        Assert.Single(batches);
        Assert.Equal([1], completions);
    }

    [Fact]
    public async Task BinarySearchFiles_StreamsBatchesAndResult()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var batches = new List<SearchResult[]>();
        var search = client.SearchFilesAsync(
            new SearchOptions
            {
                Query = "alpha",
                SearchPath = @"C:\Users\Public",
                SearchId = "search-test",
            },
            batches.Add);

        var request = await server.ReadRequestAsync();
        var result = new SearchResult
        {
            Name = "alpha.txt",
            Path = @"C:\Users\Public\alpha.txt",
            Extension = "txt",
            MatchType = "name",
            Modified = "2026-08-25 12:00",
        };
        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeSearchResultsBatch("search-test", [result]));
        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeSearchResultsResult(request.Id, [result]));

        var results = await search;
        Assert.Single(batches);
        Assert.Equal("alpha.txt", batches[0][0].Name);
        Assert.Single(results);
        Assert.Equal(@"C:\Users\Public\alpha.txt", results[0].Path);
    }

    [Fact]
    public async Task BinaryThumbnailResponses_ResolvePendingCalls()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var single = client.GenerateThumbnailAsync(@"C:\img\a.jpg", 128);
        var singleRequest = await server.ReadRequestAsync();
        var bytes = System.Text.Encoding.UTF8.GetBytes("abc123");
        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeThumbnailResult(singleRequest.Id, bytes));
        Assert.Equal(bytes, await single);

        var batch = client.GenerateThumbnailsAsync([@"C:\img\a.jpg"], 128);
        var batchRequest = await server.ReadRequestAsync();
        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeThumbnailResultsResult(
            batchRequest.Id,
            [new ThumbnailResult { Path = @"C:\img\a.jpg", Data = bytes }]));

        var results = await batch;
        Assert.Single(results);
        Assert.Equal(bytes, results[0].Data);
    }

    [Fact]
    public async Task DriveListMethods_UseFullAndLightModes()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var full = client.ListDrivesAsync();
        var fullRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.ListDrivesMethod, fullRequest.Method);
        var fullParams = Assert.IsType<JsonElement>(fullRequest.Params);
        Assert.False(fullParams.TryGetProperty("mode", out _));
        await server.SendResultAsync(
            fullRequest.Id,
            new[]
            {
                new DriveInfo
                {
                    Name = "Windows (C:)",
                    Path = @"C:\",
                    DriveType = "Fixed",
                    DriveStatus = "available",
                },
            });
        Assert.Equal("Windows (C:)", Assert.Single(await full).Name);

        var light = client.ListDrivesLightAsync();
        var lightRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.ListDrivesMethod, lightRequest.Method);
        var lightParams = Assert.IsType<JsonElement>(lightRequest.Params);
        Assert.Equal("light", lightParams.GetProperty("mode").GetString());
        await server.SendResultAsync(
            lightRequest.Id,
            new[]
            {
                new DriveInfo
                {
                    Name = "Team Share (Z:)",
                    Path = @"Z:\",
                    DriveType = "Network",
                    DriveStatus = "unknown",
                },
            });
        Assert.Equal("unknown", Assert.Single(await light).DriveStatus);

        var single = client.ListDriveAsync(@"X:\");
        var singleRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.ListDrivesMethod, singleRequest.Method);
        var singleParams = Assert.IsType<JsonElement>(singleRequest.Params);
        Assert.Equal("drive", singleParams.GetProperty("mode").GetString());
        Assert.Equal(@"X:\", singleParams.GetProperty("path").GetString());
        await server.SendResultAsync(
            singleRequest.Id,
            new[]
            {
                new DriveInfo
                {
                    Name = "Team Share (X:)",
                    Path = @"X:\",
                    DriveType = "Network",
                    DriveStatus = "available",
                },
            });
        Assert.Equal("available", Assert.Single(await single).DriveStatus);
    }

    [Fact]
    public async Task WatchAndSearchCancellation_UseNamedMethods()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var watch = client.WatchDirectoryAsync(@"C:\Users\Public");
        var watchRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.WatchDirectoryMethod, watchRequest.Method);
        await server.SendResultAsync(watchRequest.Id, null);
        await watch;

        var cancel = client.CancelSearchAsync("search-test");
        var cancelRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.CancelSearchMethod, cancelRequest.Method);
        var cancelParams = Assert.IsType<JsonElement>(cancelRequest.Params);
        Assert.Equal("search-test", cancelParams.GetProperty("searchId").GetString());
        await server.SendResultAsync(cancelRequest.Id, null);
        await cancel;

        var unwatch = client.UnwatchDirectoryAsync();
        var unwatchRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.UnwatchDirectoryMethod, unwatchRequest.Method);
        await server.SendResultAsync(unwatchRequest.Id, null);
        await unwatch;
    }

    [Fact]
    public async Task SettingsMethods_UseContractNamesAndCasing()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var get = client.GetDbSettingAsync("winui.workspace.layout.v1");
        var getRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.GetDbSettingMethod, getRequest.Method);
        var getParams = Assert.IsType<JsonElement>(getRequest.Params);
        Assert.Equal("winui.workspace.layout.v1", getParams.GetProperty("key").GetString());
        await server.SendResultAsync(getRequest.Id, "{\"version\":1}");
        Assert.Equal("{\"version\":1}", await get);

        var missing = client.GetDbSettingAsync("missing");
        var missingRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.GetDbSettingMethod, missingRequest.Method);
        await server.SendResultAsync(missingRequest.Id, null);
        Assert.Null(await missing);

        var batch = client.GetDbSettingsAsync(["theme", "missing"]);
        var batchRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.GetDbSettingsMethod, batchRequest.Method);
        var batchParams = Assert.IsType<JsonElement>(batchRequest.Params);
        Assert.Equal(
            ["theme", "missing"],
            batchParams.GetProperty("keys").EnumerateArray().Select(item => item.GetString()!).ToArray());
        await server.SendResultAsync(
            batchRequest.Id,
            new Dictionary<string, string?>
            {
                ["theme"] = "dark",
                ["missing"] = null,
            });
        var batchResult = await batch;
        Assert.Equal("dark", batchResult["theme"]);
        Assert.Null(batchResult["missing"]);

        var set = client.SetDbSettingAsync("winui.workspace.layout.v1", "{\"version\":1}");
        var setRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.SetDbSettingMethod, setRequest.Method);
        var setParams = Assert.IsType<JsonElement>(setRequest.Params);
        Assert.Equal("winui.workspace.layout.v1", setParams.GetProperty("key").GetString());
        Assert.Equal("{\"version\":1}", setParams.GetProperty("value").GetString());
        await server.SendResultAsync(setRequest.Id, null);
        await set;
    }

    [Fact]
    public async Task RemoteProfileMethods_UseContractNamesAndNeverReturnSecret()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var input = new RemoteProfileInput
        {
            Name = "Production SFTP",
            Protocol = "sftp",
            Host = "files.example.com",
            Port = 22,
            Username = "deploy",
            RootPath = "var/www",
            AuthKind = "password",
            PassiveMode = true,
            PrivateKeyPath = @"C:\Users\raz00\.ssh\id_ed25519",
        };

        var save = client.RemoteSaveProfileAsync(input, "not-returned");
        var saveRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.RemoteSaveProfileMethod, saveRequest.Method);
        var saveParams = Assert.IsType<JsonElement>(saveRequest.Params);
        Assert.Equal("not-returned", saveParams.GetProperty("secret").GetString());
        var profileParams = saveParams.GetProperty("profile");
        Assert.Equal("Production SFTP", profileParams.GetProperty("name").GetString());
        Assert.Equal(@"C:\Users\raz00\.ssh\id_ed25519", profileParams.GetProperty("private_key_path").GetString());
        Assert.Equal("auth_kind", profileParams.EnumerateObject().Single(property => property.Name == "auth_kind").Name);
        Assert.Equal("private_key_path", profileParams.EnumerateObject().Single(property => property.Name == "private_key_path").Name);
        Assert.False(profileParams.TryGetProperty("authKind", out _));
        Assert.False(profileParams.TryGetProperty("privateKeyPath", out _));

        await server.SendResultAsync(saveRequest.Id, new RemoteProfile
        {
            Id = "profile-1",
            Name = "Production SFTP",
            Protocol = "sftp",
            Host = "files.example.com",
            Port = 22,
            Username = "deploy",
            RootPath = "/var/www",
            AuthKind = "password",
            PassiveMode = true,
            CredentialTarget = "SumaFile.Remote.profile-1",
            PrivateKeyPath = @"C:\Users\raz00\.ssh\id_ed25519",
        });
        var saved = await save;
        Assert.Equal("profile-1", saved.Id);
        Assert.Equal("SumaFile.Remote.profile-1", saved.CredentialTarget);
        Assert.Equal(@"C:\Users\raz00\.ssh\id_ed25519", saved.PrivateKeyPath);

        var list = client.RemoteListProfilesAsync();
        var listRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.RemoteListProfilesMethod, listRequest.Method);
        await server.SendResultAsync(listRequest.Id, new[] { saved });
        var profiles = await list;
        Assert.Single(profiles);
        Assert.Equal("Production SFTP", profiles[0].Name);
    }

    [Fact]
    public async Task RemoteSessionMethods_UseContractNamesAndDirectoryListingShape()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var connect = client.RemoteConnectAsync("profile-1", "not-returned");
        var connectRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.RemoteConnectMethod, connectRequest.Method);
        var connectParams = Assert.IsType<JsonElement>(connectRequest.Params);
        Assert.Equal("profile-1", connectParams.GetProperty("profileId").GetString());
        Assert.Equal("not-returned", connectParams.GetProperty("secret").GetString());
        await server.SendResultAsync(connectRequest.Id, new RemoteSession
        {
            SessionId = "remote-session-1",
            ProfileId = "profile-1",
            Protocol = "sftp",
            RootPath = "/",
        });
        var session = await connect;
        Assert.Equal("remote-session-1", session.SessionId);

        var list = client.RemoteListDirectoryAsync(session.SessionId, "/");
        var listRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.RemoteListDirectoryMethod, listRequest.Method);
        var listParams = Assert.IsType<JsonElement>(listRequest.Params);
        Assert.Equal("remote-session-1", listParams.GetProperty("remoteSessionId").GetString());
        Assert.Equal("/", listParams.GetProperty("path").GetString());
        await server.SendResultAsync(listRequest.Id, new DirectoryListing
        {
            Path = "/",
            Parent = null,
            IsNetwork = true,
            Entries =
            [
                new FileEntry
                {
                    Name = "release",
                    Path = "/release",
                    IsDir = true,
                    Modified = "2026-09-21T00:00:00Z",
                },
            ],
        });
        var listing = await list;
        Assert.True(listing.IsNetwork);
        Assert.Equal("release", listing.Entries[0].Name);

        var disconnect = client.RemoteDisconnectAsync(session.SessionId);
        var disconnectRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.RemoteDisconnectMethod, disconnectRequest.Method);
        var disconnectParams = Assert.IsType<JsonElement>(disconnectRequest.Params);
        Assert.Equal("remote-session-1", disconnectParams.GetProperty("remoteSessionId").GetString());
        await server.SendResultAsync(disconnectRequest.Id, null);
        await disconnect;
    }

    [Fact]
    public async Task RemoteMutationMethods_UseContractNamesAndCasing()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var create = client.RemoteCreateDirectoryAsync("remote-session-1", "/", "incoming");
        var createRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.RemoteCreateDirectoryMethod, createRequest.Method);
        var createParams = Assert.IsType<JsonElement>(createRequest.Params);
        Assert.Equal("remote-session-1", createParams.GetProperty("remoteSessionId").GetString());
        Assert.Equal("/", createParams.GetProperty("path").GetString());
        Assert.Equal("incoming", createParams.GetProperty("name").GetString());
        await server.SendResultAsync(createRequest.Id, new FileEntry
        {
            Name = "incoming",
            Path = "/incoming",
            IsDir = true,
            Modified = "2026-09-21T00:00:00Z",
        });
        var created = await create;
        Assert.True(created.IsDir);
        Assert.Equal("/incoming", created.Path);

        var rename = client.RemoteRenameEntryAsync("remote-session-1", "/incoming", "archive");
        var renameRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.RemoteRenameEntryMethod, renameRequest.Method);
        var renameParams = Assert.IsType<JsonElement>(renameRequest.Params);
        Assert.Equal("remote-session-1", renameParams.GetProperty("remoteSessionId").GetString());
        Assert.Equal("/incoming", renameParams.GetProperty("path").GetString());
        Assert.Equal("archive", renameParams.GetProperty("newName").GetString());
        await server.SendResultAsync(renameRequest.Id, new FileEntry
        {
            Name = "archive",
            Path = "/archive",
            IsDir = true,
            Modified = "2026-09-21T00:00:00Z",
        });
        var renamed = await rename;
        Assert.Equal("archive", renamed.Name);
        Assert.Equal("/archive", renamed.Path);

        var delete = client.RemoteDeleteEntriesAsync("remote-session-1", ["/archive"]);
        var deleteRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.RemoteDeleteEntriesMethod, deleteRequest.Method);
        var deleteParams = Assert.IsType<JsonElement>(deleteRequest.Params);
        Assert.Equal("remote-session-1", deleteParams.GetProperty("remoteSessionId").GetString());
        Assert.Equal("/archive", deleteParams.GetProperty("paths")[0].GetString());
        await server.SendResultAsync(deleteRequest.Id, new[] { "/archive" });
        var deleted = await delete;
        Assert.Equal(["/archive"], deleted);
    }

    [Fact]
    public async Task RemoteTransferMethods_UseContractNamesAndCasing()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var download = client.RemoteDownloadFileAsync("remote-session-1", "/release/app.txt", @"C:\Temp\app.txt");
        var downloadRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.RemoteDownloadFileMethod, downloadRequest.Method);
        var downloadParams = Assert.IsType<JsonElement>(downloadRequest.Params);
        Assert.Equal("remote-session-1", downloadParams.GetProperty("remoteSessionId").GetString());
        Assert.Equal("/release/app.txt", downloadParams.GetProperty("remotePath").GetString());
        Assert.Equal(@"C:\Temp\app.txt", downloadParams.GetProperty("localPath").GetString());
        await server.SendResultAsync(downloadRequest.Id, @"C:\Temp\app.txt");
        Assert.Equal(@"C:\Temp\app.txt", await download);

        var upload = client.RemoteUploadFileAsync("remote-session-1", @"C:\Temp\app.txt", "/incoming/app.txt");
        var uploadRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.RemoteUploadFileMethod, uploadRequest.Method);
        var uploadParams = Assert.IsType<JsonElement>(uploadRequest.Params);
        Assert.Equal("remote-session-1", uploadParams.GetProperty("remoteSessionId").GetString());
        Assert.Equal(@"C:\Temp\app.txt", uploadParams.GetProperty("localPath").GetString());
        Assert.Equal("/incoming/app.txt", uploadParams.GetProperty("remotePath").GetString());
        await server.SendResultAsync(uploadRequest.Id, new FileEntry
        {
            Name = "app.txt",
            Path = "/incoming/app.txt",
            Modified = "2026-09-21T00:00:00Z",
            Size = 11,
        });
        var uploaded = await upload;
        Assert.Equal("app.txt", uploaded.Name);
        Assert.Equal("/incoming/app.txt", uploaded.Path);
    }

    [Fact]
    public async Task InspectionMethods_UseContractNamesAndCasing()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var preview = client.ReadFilePreviewAsync(@"C:\Users\Public\notes.txt", 2048);
        var previewRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.ReadFilePreviewMethod, previewRequest.Method);
        var previewParams = Assert.IsType<JsonElement>(previewRequest.Params);
        Assert.Equal(@"C:\Users\Public\notes.txt", previewParams.GetProperty("path").GetString());
        Assert.Equal(2048ul, previewParams.GetProperty("maxSize").GetUInt64());
        await server.SendResultAsync(previewRequest.Id, new FilePreview
        {
            FileType = "text",
            MimeType = "text/plain",
            Content = "hello",
            Encoding = "utf-8",
            Size = 5,
        });
        Assert.Equal("hello", (await preview).Content);

        var compare = client.CompareFilesAsync(@"C:\left.txt", @"C:\right.txt");
        var compareRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.CompareFilesMethod, compareRequest.Method);
        var compareParams = Assert.IsType<JsonElement>(compareRequest.Params);
        Assert.Equal(@"C:\left.txt", compareParams.GetProperty("pathA").GetString());
        Assert.Equal(@"C:\right.txt", compareParams.GetProperty("pathB").GetString());
        await server.SendResultAsync(compareRequest.Id, new FileComparison
        {
            LeftPath = @"C:\left.txt",
            RightPath = @"C:\right.txt",
            LeftName = "left.txt",
            RightName = "right.txt",
            Identical = false,
            Added = 1,
            Rows =
            [
                new DiffRow { Kind = "added", RightLine = 1, RightText = "hello" },
            ],
        });
        var comparison = await compare;
        Assert.False(comparison.Identical);
        Assert.Single(comparison.Rows);

        var checksum = client.ComputeChecksumAsync(@"C:\left.txt");
        var checksumRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.ComputeChecksumMethod, checksumRequest.Method);
        await server.SendResultAsync(checksumRequest.Id, new Checksums
        {
            Md5 = "md5",
            Sha1 = "sha1",
            Sha256 = "sha256",
        });
        Assert.Equal("sha256", (await checksum).Sha256);
    }

    [Fact]
    public async Task ArchiveMethods_UseContractNamesAndCasing()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var list = client.ListArchiveAsync(@"C:\pack.zip");
        var listRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.ListArchiveMethod, listRequest.Method);
        var listParams = Assert.IsType<JsonElement>(listRequest.Params);
        Assert.Equal(@"C:\pack.zip", listParams.GetProperty("path").GetString());
        await server.SendResultAsync(listRequest.Id, new ArchiveInfo
        {
            Path = @"C:\pack.zip",
            Format = "zip",
            Entries =
            [
                new ArchiveEntry { Name = "notes.txt", Path = "notes.txt", Size = 5, CompressedSize = 4 },
            ],
            TotalSize = 5,
            CompressedSize = 4,
        });
        Assert.Equal("zip", (await list).Format);

        var capabilities = client.GetArchiveCapabilitiesAsync();
        var capabilitiesRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.GetArchiveCapabilitiesMethod, capabilitiesRequest.Method);
        await server.SendResultAsync(capabilitiesRequest.Id, new ArchiveCapabilities
        {
            Formats =
            [
                new ArchiveFormatCapability
                {
                    Format = "rar",
                    Extension = ".rar",
                    CanList = true,
                    CanExtract = true,
                    CanCreate = false,
                    CanModify = false,
                    Engine = "built-in-unrar",
                    Note = "read only",
                },
            ],
        });
        Assert.False((await capabilities).Formats[0].CanCreate);

        var extract = client.ExtractArchiveAsync(@"C:\pack.zip", @"C:\out");
        var extractRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.ExtractArchiveMethod, extractRequest.Method);
        var extractParams = Assert.IsType<JsonElement>(extractRequest.Params);
        Assert.Equal(@"C:\pack.zip", extractParams.GetProperty("archivePath").GetString());
        Assert.Equal(@"C:\out", extractParams.GetProperty("destination").GetString());
        await server.SendResultAsync(extractRequest.Id, null);
        await extract;

        var create = client.CreateArchiveAsync([@"C:\a.txt"], @"C:\pack.zip", "zip");
        var createRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.CreateArchiveMethod, createRequest.Method);
        var createParams = Assert.IsType<JsonElement>(createRequest.Params);
        Assert.Equal(@"C:\pack.zip", createParams.GetProperty("archivePath").GetString());
        Assert.Equal("zip", createParams.GetProperty("format").GetString());
        Assert.Equal(@"C:\a.txt", createParams.GetProperty("paths")[0].GetString());
        await server.SendResultAsync(createRequest.Id, null);
        await create;
    }

    [Fact]
    public async Task Cancellation_AbandonsAwaitWithoutSendingCancel()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        using var cancelled = new CancellationTokenSource();
        var invoke = client.GetHomeDirAsync(cancelled.Token);
        var request = await server.ReadRequestAsync();
        Assert.Equal(Protocol.GetHomeDirMethod, request.Method);

        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoke);
        Assert.Equal(0, client.InFlightCount);

        await server.SendResultAsync(request.Id, @"C:\Users\test");

        var next = client.GetAppVersionAsync();
        var nextRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.GetAppVersionMethod, nextRequest.Method);
        Assert.NotEqual(request.Id, nextRequest.Id);
        await server.SendResultAsync(nextRequest.Id, "1.0.1");
        Assert.Equal("1.0.1", await next);
    }

    [Fact]
    public async Task TypedErrors_PreserveCodeAndMessage()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var conflict = client.InvokeAsync<string>("copy_entry", new { });
        var conflictRequest = await server.ReadRequestAsync();
        await server.SendErrorAsync(
            conflictRequest.Id,
            Protocol.ErrApplication,
            "CONFLICT: destination already exists: C:\\dest\\copy.txt");
        var conflictError = await Assert.ThrowsAsync<IpcException>(() => conflict);
        Assert.True(conflictError.IsConflict);
        Assert.Equal(Protocol.ErrApplication, conflictError.Code);
        Assert.StartsWith("CONFLICT:", conflictError.Message, StringComparison.Ordinal);

        var hostOwned = client.SelectDirectoryAsync();
        var hostRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.SelectDirectoryMethod, hostRequest.Method);
        await server.SendErrorAsync(hostRequest.Id, Protocol.ErrHostOwned, "HOST_OWNED: select_directory");
        var hostError = await Assert.ThrowsAsync<IpcException>(() => hostOwned);
        Assert.True(hostError.IsHostOwned);
        Assert.Equal(Protocol.ErrHostOwned, hostError.Code);
    }

    [Fact]
    public async Task TypedErrors_PreserveStructuredData()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var copy = client.CopyWithProgressAsync(
            [@"C:\a.txt"],
            @"C:\dest",
            "op-test",
            "skip");
        var request = await server.ReadRequestAsync();
        await server.SendErrorAsync(
            request.Id,
            Protocol.ErrApplication,
            "copy failed",
            new
            {
                operation_id = "op-test",
                status = "failed",
                committed = new[] { new { source = @"C:\a.txt", destination = @"C:\dest\a.txt" } },
                skipped = Array.Empty<object>(),
                failed = new[] { new { source = @"C:\b.txt", error = "missing" } },
                pending = new[] { @"C:\c.txt" },
            });

        var error = await Assert.ThrowsAsync<IpcException>(() => copy);
        Assert.NotNull(error.ErrorData);
        Assert.Equal("op-test", error.ErrorData.Value.GetProperty("operation_id").GetString());
        Assert.Equal(@"C:\dest\a.txt", error.ErrorData.Value.GetProperty("committed")[0].GetProperty("destination").GetString());
    }

    [Fact]
    public async Task Disconnect_FailsInFlightAndRaisesEvent()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;

        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, error) => disconnected.TrySetResult(error);

        var invoke = client.GetHomeDirAsync();
        _ = await server.ReadRequestAsync();
        await server.DisposeAsync();

        var error = await Assert.ThrowsAsync<IpcException>(() => invoke);
        Assert.Equal(IpcErrorKind.Transport, error.Kind);
        Assert.False(client.IsConnected);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await client.DisposeAsync();
    }

    [Fact]
    public async Task ShutdownAndShowMainWindow_AcceptNullResult()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var shutdown = client.ShutdownAsync();
        var shutdownRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.ShutdownMethod, shutdownRequest.Method);
        await server.SendResultAsync(shutdownRequest.Id, null);
        await shutdown;

        var show = client.ShowMainWindowAsync();
        var showRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.ShowMainWindowMethod, showRequest.Method);
        await server.SendResultAsync(showRequest.Id, null);
        await show;
    }
}
