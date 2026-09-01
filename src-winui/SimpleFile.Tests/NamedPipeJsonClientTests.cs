using System.Text.Json;
using SimpleFile.Ipc;
using Xunit;

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
                AppVersion = "1.0.0",
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
            AppVersion = "1.0.0",
        });

        var health = await healthTask;
        Assert.True(health.Ok);
        Assert.Equal("1.0.0", health.AppVersion);
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
        var entry = Assert.Single(listing.Entries);
        Assert.Equal(@"C:\Users\Public\notes.txt", entry.Path);
        Assert.Equal("txt", entry.Extension);
        Assert.Single(seen);
        Assert.Equal(@"C:\Users\Public\notes.txt", seen[0].Entries[0].Path);
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
            new[]
            {
                new SearchResult { Name = "alpha.txt", Path = @"C:\Users\Public\alpha.txt" },
            });
        await server.SendNotificationAsync(Protocol.SearchCompleteEvent, 1);
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
            new[]
            {
                new SearchResult { Name = "beta.txt", Path = @"C:\Users\Public\beta.txt" },
            });
        await server.SendNotificationAsync(Protocol.SearchCompleteEvent, 2);
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
        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeSearchResultsBatch([result]));
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
        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeThumbnailResult(singleRequest.Id, "abc123"));
        Assert.Equal("abc123", await single);

        var batch = client.GenerateThumbnailsAsync([@"C:\img\a.jpg"], 128);
        var batchRequest = await server.ReadRequestAsync();
        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeThumbnailResultsResult(
            batchRequest.Id,
            [new ThumbnailResult { Path = @"C:\img\a.jpg", Data = "abc123" }]));

        var results = await batch;
        Assert.Single(results);
        Assert.Equal("abc123", results[0].Data);
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
        await server.SendResultAsync(nextRequest.Id, "1.0.0");
        Assert.Equal("1.0.0", await next);
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
