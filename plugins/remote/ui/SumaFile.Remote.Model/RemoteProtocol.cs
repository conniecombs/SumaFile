using System.Text.Json.Serialization;

namespace SumaFile.Remote.Model;

/// <summary>
/// The backend methods of plugins/remote/protocol.json, plus the pipe protocol's <c>cancel</c>.
/// <c>scripts/check-ipc-schema.mjs</c> requires every method and event there to be a literal in this file.
/// </summary>
public static class RemoteMethods
{
    public const string ListProfiles = "profiles.list";
    public const string SaveProfile = "profiles.save";
    public const string DeleteProfile = "profiles.delete";
    public const string TestProfile = "profiles.test";
    public const string Connect = "session.connect";
    public const string Disconnect = "session.disconnect";
    public const string ListDirectory = "fs.list";
    public const string CreateDirectory = "fs.create_directory";
    public const string Rename = "fs.rename";
    public const string DeleteEntries = "fs.delete";
    public const string Transfer = "transfer.run";
    public const string Cancel = "cancel";
}

/// <summary>The backend events of plugins/remote/protocol.json.</summary>
public static class RemoteEvents
{
    public const string TransferProgress = "remote.transfer-progress";
    public const string TransferTuning = "remote.transfer-tuning";
    public const string ProfilesChanged = "remote.profiles-changed";
}

// The camelCase params of the methods that take more than the transfer request.

internal sealed record ProfileParams(
    [property: JsonPropertyName("profile")] RemoteProfileInput Profile,
    [property: JsonPropertyName("secret")] string? Secret);

internal sealed record ProfileIdParams([property: JsonPropertyName("profileId")] string ProfileId);

internal sealed record ConnectParams(
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("secret")] string? Secret);

internal sealed record SessionParams([property: JsonPropertyName("remoteSessionId")] string RemoteSessionId);

internal sealed record PathParams(
    [property: JsonPropertyName("remoteSessionId")] string RemoteSessionId,
    [property: JsonPropertyName("path")] string Path);

internal sealed record CreateDirectoryParams(
    [property: JsonPropertyName("remoteSessionId")] string RemoteSessionId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string Name);

internal sealed record RenameParams(
    [property: JsonPropertyName("remoteSessionId")] string RemoteSessionId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("newName")] string NewName);

internal sealed record DeleteParams(
    [property: JsonPropertyName("remoteSessionId")] string RemoteSessionId,
    [property: JsonPropertyName("paths")] IReadOnlyList<string> Paths);

internal sealed record CancelParams([property: JsonPropertyName("operationId")] string OperationId);
