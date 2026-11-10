using System.Text.Json;
using SimpleFile.Tests.Remote;
using SumaFile.Git.Model;
using Xunit;

namespace SimpleFile.Tests.Git;

public class GitBackendClientTests
{
    private const string StatusJson = """
        {"is_repo":true,"root":"R:\\repo","branch":"main","upstream":"origin/main","head":"abc1234","ahead":1,"behind":2,
         "staged":1,"unstaged":1,"untracked":1,"conflicted":0,
         "changes":[{"path":"café 資料/a.txt","absolute_path":"R:\\repo\\café 資料\\a.txt","original_path":null,"status":"modified",
                     "index_status":" ","worktree_status":"M","staged":false,"unstaged":true,"untracked":false,"conflicted":false}]}
        """;

    private const string ResultJson = """{"command":"git fetch","exit_code":0,"stdout":"done","stderr":"","summary":"Fetched"}""";

    [Fact]
    public async Task EachMethod_SendsItsProtocolNameAndCamelCaseParams()
    {
        var host = new FakePluginHost("com.sumafile.git");
        Assert.Equal("com.sumafile.git", host.PluginId);
        host.Respond(GitMethods.Status, StatusJson);
        host.Respond(GitMethods.Decorations, """{"in_repository":true,"root":"R:\\repo","rows":[{"path":"R:\\repo\\a.txt","status":"modified"}]}""");
        foreach (var method in new[] { GitMethods.Stage, GitMethods.Unstage, GitMethods.Discard, GitMethods.Commit, GitMethods.Fetch, GitMethods.Pull, GitMethods.Push })
        {
            host.Respond(method, ResultJson);
        }

        host.Respond(GitMethods.Diff, "\"diff --git a/a.txt b/a.txt\"");
        var client = new GitBackendClient(host);
        string[] paths = [@"R:\repo\café 資料\a.txt", @"R:\repo\-n.txt"];

        var status = await client.StatusAsync(@"R:\repo", CancellationToken.None);
        var decorations = await client.DecorationsAsync(@"R:\repo", CancellationToken.None);
        await client.StageAsync(@"R:\repo", paths, CancellationToken.None);
        await client.UnstageAsync(@"R:\repo", paths, CancellationToken.None);
        await client.DiscardAsync(@"R:\repo", paths, CancellationToken.None);
        var diff = await client.DiffAsync(@"R:\repo", paths[0], CancellationToken.None);
        await client.CommitAsync(@"R:\repo", "Fix [brackets] & spaces", CancellationToken.None);
        var fetched = await client.FetchAsync(@"R:\repo", CancellationToken.None);
        await client.PullAsync(@"R:\repo", CancellationToken.None);
        await client.PushAsync(@"R:\repo", CancellationToken.None);

        var calls = host.Calls;
        Assert.Equal(
            ["repo.status", "repo.decorations", "paths.stage", "paths.unstage", "paths.discard", "paths.diff", "repo.commit", "remote.fetch", "remote.pull", "remote.push"],
            calls.Select(call => call.Method).ToArray());
        const string pathsJson = """{"path":"R:\\repo","paths":["R:\\repo\\café 資料\\a.txt","R:\\repo\\-n.txt"]}""";
        Assert.Equal("""{"path":"R:\\repo"}""", Normalize(calls[0].ArgsJson));
        Assert.Equal("""{"folder":"R:\\repo"}""", Normalize(calls[1].ArgsJson));
        Assert.Equal(pathsJson, Normalize(calls[2].ArgsJson));
        Assert.Equal(pathsJson, Normalize(calls[3].ArgsJson));
        Assert.Equal(pathsJson, Normalize(calls[4].ArgsJson));
        Assert.Equal("""{"path":"R:\\repo","filePath":"R:\\repo\\café 資料\\a.txt"}""", Normalize(calls[5].ArgsJson));

        // Commit, fetch, pull and push carry a fresh operation id each, the one a cancel would name.
        var operationIds = new List<string>();
        foreach (var (call, keys) in new[]
        {
            (calls[6], new[] { "path", "message", "operationId" }),
            (calls[7], new[] { "path", "operationId" }),
            (calls[8], new[] { "path", "operationId" }),
            (calls[9], new[] { "path", "operationId" }),
        })
        {
            using var sent = JsonDocument.Parse(call.ArgsJson);
            Assert.Equal(keys, sent.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(@"R:\repo", sent.RootElement.GetProperty("path").GetString());
            var operationId = sent.RootElement.GetProperty("operationId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(operationId));
            operationIds.Add(operationId!);
        }

        Assert.Equal(4, operationIds.Distinct(StringComparer.Ordinal).Count());
        using (var commit = JsonDocument.Parse(calls[6].ArgsJson))
        {
            Assert.Equal("Fix [brackets] & spaces", commit.RootElement.GetProperty("message").GetString());
        }

        // Nothing was cancelled, so no cancel went out.
        Assert.DoesNotContain(calls, call => call.Method == GitMethods.Cancel);

        // The results read their snake_case shapes.
        Assert.True(status.IsRepo);
        Assert.Equal((@"R:\repo", "main", "origin/main", "abc1234", 1, 2), (status.Root, status.Branch, status.Upstream, status.Head, status.Ahead, status.Behind));
        var change = Assert.Single(status.Changes);
        Assert.Equal(("café 資料/a.txt", @"R:\repo\café 資料\a.txt", "modified", true, false), (change.Path, change.AbsolutePath, change.Status, change.Unstaged, change.Untracked));
        Assert.True(decorations.InRepository);
        Assert.Equal(@"R:\repo", decorations.Root);
        var row = Assert.Single(decorations.Rows);
        Assert.Equal((@"R:\repo\a.txt", "modified"), (row.Path, row.Status));
        Assert.Equal("diff --git a/a.txt b/a.txt", diff);
        Assert.Equal(("git fetch", 0, "done", "Fetched"), (fetched.Command, fetched.ExitCode, fetched.Stdout, fetched.Summary));

        // When the backend's protocol.json is in the tree, every call's params are exactly the keys it documents.
        if (GitProtocolPath() is { } protocolPath)
        {
            using var protocol = JsonDocument.Parse(File.ReadAllText(protocolPath));
            Assert.Equal("com.sumafile.git", protocol.RootElement.GetProperty("plugin").GetString());
            var methods = protocol.RootElement.GetProperty("methods");
            Assert.Equal(
                methods.EnumerateObject().Select(method => method.Name).Order(StringComparer.Ordinal),
                calls.Select(call => call.Method).Order(StringComparer.Ordinal));
            foreach (var call in calls)
            {
                var documented = methods.GetProperty(call.Method).GetProperty("params");
                using var sent = JsonDocument.Parse(call.ArgsJson);
                Assert.Equal(
                    documented.EnumerateObject().Select(param => param.Name).Order(StringComparer.Ordinal),
                    sent.RootElement.EnumerateObject().Select(param => param.Name).Order(StringComparer.Ordinal));
            }

            // The DTOs name exactly the documented result fields, in order.
            var types = protocol.RootElement.GetProperty("types");
            foreach (var type in new[] { typeof(GitRepositoryStatus), typeof(GitFileStatus), typeof(GitCommandResult), typeof(GitFolderDecorations), typeof(GitDecorationRow) })
            {
                var documented = types.GetProperty(type.Name).GetProperty("fields").EnumerateArray().Select(field => field.GetString()!).ToArray();
                var declared = type.GetProperties()
                    .Select(property => property.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false)
                        .Cast<System.Text.Json.Serialization.JsonPropertyNameAttribute>().Single().Name)
                    .ToArray();
                Assert.Equal(documented, declared);
            }
        }
    }

    [Fact]
    public async Task Fetch_CancelledByTheCaller_SendsCancelWithItsOperationId()
    {
        var host = new FakePluginHost("com.sumafile.git");
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Responders[GitMethods.Fetch] = _ => reply.Task;
        var client = new GitBackendClient(host);
        using var cancellation = new CancellationTokenSource();

        var running = client.FetchAsync(@"R:\repo", cancellation.Token);
        cancellation.Cancel();
        cancellation.Cancel();

        // The cancel goes out at once and names the fetch's own operation id; the call keeps waiting for the backend.
        var fetch = Assert.Single(host.Calls, call => call.Method == GitMethods.Fetch);
        using var fetchArgs = JsonDocument.Parse(fetch.ArgsJson);
        var operationId = fetchArgs.RootElement.GetProperty("operationId").GetString();
        var cancel = Assert.Single(host.Calls, call => call.Method == GitMethods.Cancel);
        Assert.Equal($$"""{"operationId":"{{operationId}}"}""", cancel.ArgsJson);
        Assert.False(running.IsCompleted);

        // The backend ends git and fails the call with its cancelled text, which reaches the caller unchanged.
        reply.SetException(new SimpleFile.Ipc.IpcException(-32000, GitErrors.Cancelled));
        var failure = await Assert.ThrowsAsync<SimpleFile.Ipc.IpcException>(() => running);
        Assert.Equal(GitErrors.Cancelled, failure.Message);
        Assert.Single(host.Calls, call => call.Method == GitMethods.Cancel);
    }

    [Fact]
    public async Task Fetch_AlreadyCancelled_SendsNothing()
    {
        var host = new FakePluginHost("com.sumafile.git");
        var client = new GitBackendClient(host);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PushAsync(@"R:\repo", new CancellationToken(canceled: true)));

        Assert.Empty(host.Calls);
    }

    private static string Normalize(string json) =>
        JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement,
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string? GitProtocolPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "plugins", "git")))
            {
                var candidate = Path.Combine(directory.FullName, "plugins", "git", "protocol.json");
                return File.Exists(candidate) ? candidate : null;
            }
        }

        return null;
    }
}
