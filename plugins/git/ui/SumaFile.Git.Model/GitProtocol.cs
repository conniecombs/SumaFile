using System.Text.Json.Serialization;

namespace SumaFile.Git.Model;

/// <summary>
/// The backend methods of plugins/git/protocol.json, plus the pipe protocol's <c>cancel</c>.
/// <c>scripts/check-ipc-schema.mjs</c> requires every method there to be a literal in this file.
/// </summary>
public static class GitMethods
{
    public const string Status = "repo.status";
    public const string Decorations = "repo.decorations";
    public const string Stage = "paths.stage";
    public const string Unstage = "paths.unstage";
    public const string Discard = "paths.discard";
    public const string Diff = "paths.diff";
    public const string Commit = "repo.commit";
    public const string Fetch = "remote.fetch";
    public const string Pull = "remote.pull";
    public const string Push = "remote.push";
    public const string Cancel = "cancel";
}

/// <summary>Error texts of plugins/git/protocol.json that the UI matches; byte-identical to the backend's.</summary>
public static class GitErrors
{
    /// <summary>Every call fails with this when no <c>git.exe</c> is on an absolute <c>PATH</c> entry.</summary>
    public const string NotFound = "Git was not found. Install Git for Windows or add git.exe to PATH.";

    /// <summary>A commit, fetch, pull or push stopped by a <c>cancel</c> of its operation id or its Transfers row.</summary>
    public const string Cancelled = "Git operation cancelled";
}

// The camelCase params of each method.

internal sealed record PathParams([property: JsonPropertyName("path")] string Path);

internal sealed record FolderParams([property: JsonPropertyName("folder")] string Folder);

internal sealed record PathsParams(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("paths")] IReadOnlyList<string> Paths);

internal sealed record DiffParams(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("filePath")] string FilePath);

internal sealed record CommitParams(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("operationId")] string OperationId);

internal sealed record OperationParams(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("operationId")] string OperationId);

internal sealed record CancelParams([property: JsonPropertyName("operationId")] string OperationId);
