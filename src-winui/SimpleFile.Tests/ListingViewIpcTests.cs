using System.Text.Json;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>The listing view methods on the wire: their parameters, the binary page frame and the change event.</summary>
public class ListingViewIpcTests
{
    private static ListingViewPage SamplePage() => new()
    {
        ViewId = "pane-1",
        Path = @"C:\Users\Public",
        Parent = @"C:\Users",
        IsNetwork = false,
        Total = 12345,
        Revision = 0x1_0000_0002,
        Offset = 512,
        Entries =
        [
            // An ordinary folder's rows carry names only.
            new FileEntry { Name = "Documents", IsDir = true, Modified = "2026-11-01 10:00" },
            new FileEntry { Name = "notes.txt", Size = 42, IsHidden = true },
            new FileEntry { Name = "pagefile.sys", Size = 9, IsHidden = true, IsSystem = true, Permissions = "r--" },
            new FileEntry { Name = "link", IsSymlink = true, SymlinkTarget = @"D:\target" },
        ],
    };

    [Fact]
    public async Task OpenListingView_SendsThePanesOrderAndFilterAndRebuildsThePathsOfTheBinaryPage()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var previews = new List<DirectoryListingChunk>();
        var open = client.OpenListingViewAsync(
            new ListingViewRequest
            {
                ViewId = "pane-1",
                Path = @"C:\Users\Public",
                SortBy = "size",
                SortAscending = false,
                FoldersFirst = false,
                IncludeHidden = false,
                Filter = "txt",
                Locale = "en-US",
                PageSize = 512,
            },
            previews.Add);
        var request = await server.ReadRequestAsync();

        Assert.Equal(Protocol.OpenListingViewMethod, request.Method);
        var parameters = Assert.IsType<JsonElement>(request.Params);
        Assert.Equal("pane-1", parameters.GetProperty("viewId").GetString());
        Assert.Equal(@"C:\Users\Public", parameters.GetProperty("path").GetString());
        Assert.Equal("size", parameters.GetProperty("sortBy").GetString());
        Assert.False(parameters.GetProperty("sortAscending").GetBoolean());
        Assert.False(parameters.GetProperty("foldersFirst").GetBoolean());
        Assert.False(parameters.GetProperty("includeHidden").GetBoolean());
        Assert.Equal("txt", parameters.GetProperty("filter").GetString());
        Assert.Equal("en-US", parameters.GetProperty("locale").GetString());
        Assert.Equal(512u, parameters.GetProperty("pageSize").GetUInt32());
        // Not asked for: the service keeps the view it has when the folder is the same, and a navigation is not
        // limited to the view the pane has.
        Assert.False(parameters.TryGetProperty("reload", out var reload) && reload.ValueKind == JsonValueKind.True);
        Assert.False(parameters.TryGetProperty("reuse", out var reuse) && reuse.ValueKind == JsonValueKind.True);

        // A slow folder previews its first entries on the open's request id before the page.
        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeDirectoryListingChunk(
            new DirectoryListingChunkNotification
            {
                RequestId = request.Id,
                Path = @"C:\Users\Public",
                Entries = [new FileEntry { Name = "early.txt" }],
                ChunkIndex = 0,
            }));
        await server.SendBinaryFrameAsync(BinaryFrameCodec.EncodeListingViewPage(request.Id, SamplePage()));

        var page = await open;
        Assert.Equal(@"C:\Users\Public\early.txt", Assert.Single(previews).Entries[0].Path);
        Assert.Equal(("pane-1", 12345u, 0x1_0000_0002ul, 512u), (page.ViewId, page.Total, page.Revision, page.Offset));
        Assert.Equal(@"C:\Users", page.Parent);
        Assert.Equal(
            [@"C:\Users\Public\Documents", @"C:\Users\Public\notes.txt", @"C:\Users\Public\pagefile.sys", @"C:\Users\Public\link"],
            page.Entries.Select(entry => entry.Path));
        Assert.Equal(["", "txt", "sys", ""], page.Entries.Select(entry => entry.Extension));
        Assert.Equal([true, false, false, false], page.Entries.Select(entry => entry.IsDir));
        // The page frame carries the attributes the list_directory frames drop.
        Assert.Equal([false, true, true, false], page.Entries.Select(entry => entry.IsHidden));
        Assert.Equal([false, false, true, false], page.Entries.Select(entry => entry.IsSystem));
        Assert.Equal([false, false, false, true], page.Entries.Select(entry => entry.IsSymlink));
        Assert.Equal(42ul, page.Entries[1].Size);
        Assert.Equal("r--", page.Entries[2].Permissions);
        Assert.Equal(@"D:\target", page.Entries[3].SymlinkTarget);
        Assert.Equal("2026-11-01 10:00", page.Entries[0].Modified);
    }

    [Fact]
    public async Task GetListingViewRange_ReadsAJsonPageToo()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var range = client.GetListingViewRangeAsync("pane-1", 512, 4096);
        var request = await server.ReadRequestAsync();
        Assert.Equal(Protocol.GetListingViewRangeMethod, request.Method);
        var parameters = Assert.IsType<JsonElement>(request.Params);
        Assert.Equal(("pane-1", 512u, 4096u), (
            parameters.GetProperty("viewId").GetString(),
            parameters.GetProperty("offset").GetUInt32(),
            parameters.GetProperty("count").GetUInt32()));

        // Without binary frames the page is plain JSON with the service's snake_case names.
        await server.SendResultAsync(request.Id, new
        {
            view_id = "pane-1",
            path = @"C:\Users\Public",
            parent = (string?)null,
            is_network = true,
            total = 600,
            revision = 7,
            offset = 512,
            entries = new[] { new { name = "z.log", path = "", is_dir = false, is_symlink = false, is_hidden = true, size = 3, modified = "", extension = "" } },
        });

        var page = await range;
        Assert.Equal((600u, 7ul, 512u, true), (page.Total, page.Revision, page.Offset, page.IsNetwork));
        var entry = Assert.Single(page.Entries);
        Assert.Equal((@"C:\Users\Public\z.log", "log", true), (entry.Path, entry.Extension, entry.IsHidden));
    }

    [Fact]
    public async Task RefreshAndClose_NameTheViewAndTheChangeEventArrivesAsADelta()
    {
        var (server, client) = await FakeIpcServer.ConnectAsync();
        await using var serverLifetime = server;
        await using var clientLifetime = client;

        var seen = new TaskCompletionSource<ListingViewDelta>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = client.On<ListingViewDelta>(Protocol.ListingViewChangedEvent, seen.SetResult);

        var refresh = client.RefreshListingViewAsync("pane-1");
        var request = await server.ReadRequestAsync();
        Assert.Equal(Protocol.RefreshListingViewMethod, request.Method);
        Assert.Equal("pane-1", Assert.IsType<JsonElement>(request.Params).GetProperty("viewId").GetString());
        var wire = new
        {
            view_id = "pane-1",
            base_revision = 4,
            revision = 5,
            total = 3,
            reset = false,
            changes = new object[]
            {
                new { kind = "remove", index = 2 },
                new { kind = "insert", index = 0, entry = new { name = "a.txt", path = "", is_dir = false, is_symlink = false, size = 1, modified = "", extension = "" } },
            },
        };
        await server.SendResultAsync(request.Id, wire);

        var delta = await refresh;
        Assert.Equal(("pane-1", 4ul, 5ul, 3u, false), (delta.ViewId, delta.BaseRevision, delta.Revision, delta.Total, delta.Reset));
        Assert.Equal([(ListingViewChange.Remove, 2u), (ListingViewChange.Insert, 0u)], delta.Changes.Select(change => (change.Kind, change.Index)));
        Assert.Null(delta.Changes[0].Entry);
        // A delta's entries leave out what the folder path gives, as a page's do.
        NamedPipeJsonClient.NormalizeListingViewDelta(@"C:\Users\Public", delta);
        Assert.Equal((@"C:\Users\Public\a.txt", "txt"), (delta.Changes[1].Entry!.Path, delta.Changes[1].Entry!.Extension));

        await server.SendNotificationAsync(Protocol.ListingViewChangedEvent, wire);
        var pushed = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((5ul, 2), (pushed.Revision, pushed.Changes.Count));

        var close = client.CloseListingViewAsync("pane-1");
        var closeRequest = await server.ReadRequestAsync();
        Assert.Equal(Protocol.CloseListingViewMethod, closeRequest.Method);
        Assert.Equal("pane-1", Assert.IsType<JsonElement>(closeRequest.Params).GetProperty("viewId").GetString());
        await server.SendResultAsync(closeRequest.Id, null);
        await close;
    }

    [Fact]
    public void BinaryPage_RoundTripsThroughTheFrameCodec()
    {
        var frame = BinaryFrameCodec.EncodeListingViewPage(77, SamplePage());

        Assert.True(BinaryFrameCodec.TryDecode(frame, out var message));
        Assert.Equal(77, message!.ResponseId);
        var page = Assert.IsType<ListingViewPage>(message.Payload);
        var expected = SamplePage();
        Assert.Equal(
            JsonSerializer.Serialize(expected),
            JsonSerializer.Serialize(page));
        Assert.Equal(Protocol.BinaryListingViewPage, frame[5]);
    }

    /// <summary>
    /// The frame the Rust service's encoder produces for this page (its test
    /// <c>listing_view_page_frame_matches_the_golden_bytes</c> asserts the same bytes): the two sides agree on the
    /// page frame byte for byte.
    /// </summary>
    private const string GoldenListingViewPage =
        "5346423101090700000002000000763104000000433A5C7700010300000002010000000000000100000002000000010000006400000000090000000000000000010000006D00000000000005000000662E74787408000000583A5C662E747874060102000000000000000000000300000074787401020000007277010100000074";

    [Fact]
    public void BinaryPage_DecodesTheServicesGoldenFrame()
    {
        var frame = Convert.FromHexString(GoldenListingViewPage);

        Assert.True(BinaryFrameCodec.TryDecode(frame, out var message));
        Assert.Equal(7, message!.ResponseId);
        var page = Assert.IsType<ListingViewPage>(message.Payload);
        Assert.Equal(("v1", @"C:\w", (string?)null, true, 3u, 258ul, 1u), (page.ViewId, page.Path, page.Parent, page.IsNetwork, page.Total, page.Revision, page.Offset));
        Assert.Equal(2, page.Entries.Count);
        var folder = page.Entries[0];
        Assert.Equal(("d", "", true, false, false, true, 0ul, "m"), (folder.Name, folder.Path, folder.IsDir, folder.IsSymlink, folder.IsHidden, folder.IsSystem, folder.Size, folder.Modified));
        var file = page.Entries[1];
        Assert.Equal(("f.txt", @"X:\f.txt", false, true, true, false, 513ul, "txt"), (file.Name, file.Path, file.IsDir, file.IsSymlink, file.IsHidden, file.IsSystem, file.Size, file.Extension));
        Assert.Equal(("rw", "t"), (file.Permissions, file.SymlinkTarget));
        // And the host's own encoder writes the same bytes.
        Assert.Equal(GoldenListingViewPage, Convert.ToHexString(BinaryFrameCodec.EncodeListingViewPage(7, page)));
    }
}
