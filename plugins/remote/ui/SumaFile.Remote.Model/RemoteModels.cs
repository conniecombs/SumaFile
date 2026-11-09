using System.Text.Json.Serialization;

namespace SumaFile.Remote.Model;

// The wire shapes of plugins/remote/protocol.json. Every property names its JSON field, because the host
// serializes with no naming policy: results keep the 1.0.4 snake_case shapes, params and events are camelCase.

/// <summary>A saved connection profile. It never carries a secret; secrets live in Credential Manager.</summary>
public sealed class RemoteProfile
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("root_path")] public string RootPath { get; set; } = "/";
    [JsonPropertyName("auth_kind")] public string AuthKind { get; set; } = "";
    [JsonPropertyName("insecure_plain_ftp")] public bool InsecurePlainFtp { get; set; }
    [JsonPropertyName("passive_mode")] public bool PassiveMode { get; set; }
    [JsonPropertyName("credential_target")] public string? CredentialTarget { get; set; }
    [JsonPropertyName("private_key_path")] public string? PrivateKeyPath { get; set; }
    [JsonPropertyName("trusted_host_fingerprint")] public string? TrustedHostFingerprint { get; set; }
}

/// <summary>A profile to save or test; a null <see cref="Id"/> creates a new profile.</summary>
public sealed class RemoteProfileInput
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("root_path")] public string RootPath { get; set; } = "/";
    [JsonPropertyName("auth_kind")] public string AuthKind { get; set; } = "";
    [JsonPropertyName("insecure_plain_ftp")] public bool InsecurePlainFtp { get; set; }
    [JsonPropertyName("passive_mode")] public bool PassiveMode { get; set; } = true;
    [JsonPropertyName("credential_target")] public string? CredentialTarget { get; set; }
    [JsonPropertyName("private_key_path")] public string? PrivateKeyPath { get; set; }
    [JsonPropertyName("trusted_host_fingerprint")] public string? TrustedHostFingerprint { get; set; }
}

public sealed class RemoteSession
{
    [JsonPropertyName("session_id")] public string SessionId { get; set; } = "";
    [JsonPropertyName("profile_id")] public string ProfileId { get; set; } = "";
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "";
    [JsonPropertyName("root_path")] public string RootPath { get; set; } = "/";
}

public sealed class RemoteConnectionTestResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("capabilities")] public string[] Capabilities { get; set; } = [];
}

/// <summary>A remote or local file or folder: the twelve fields of the service's <c>FileEntry</c>.</summary>
public sealed class RemoteFileEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("is_dir")] public bool IsDir { get; set; }
    [JsonPropertyName("is_symlink")] public bool IsSymlink { get; set; }
    [JsonPropertyName("is_hidden")] public bool IsHidden { get; set; }
    [JsonPropertyName("is_system")] public bool IsSystem { get; set; }
    [JsonPropertyName("size")] public ulong Size { get; set; }
    [JsonPropertyName("modified")] public string Modified { get; set; } = "";
    [JsonPropertyName("extension")] public string Extension { get; set; } = "";
    [JsonPropertyName("permissions")] public string? Permissions { get; set; }
    [JsonPropertyName("symlink_target")] public string? SymlinkTarget { get; set; }
    [JsonPropertyName("git_status")] public string? GitStatus { get; set; }
}

public sealed class RemoteDirectoryListing
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("parent")] public string? Parent { get; set; }
    [JsonPropertyName("entries")] public List<RemoteFileEntry> Entries { get; set; } = [];
    [JsonPropertyName("is_network")] public bool IsNetwork { get; set; }
}

/// <summary>The <c>transfer.run</c> params (camelCase).</summary>
public sealed class RemoteTransferRequest
{
    [JsonPropertyName("remoteSessionId")] public string RemoteSessionId { get; set; } = "";

    /// <summary><c>upload</c> or <c>download</c>.</summary>
    [JsonPropertyName("direction")] public string Direction { get; set; } = "";
    [JsonPropertyName("sources")] public string[] Sources { get; set; } = [];
    [JsonPropertyName("destination")] public string Destination { get; set; } = "";

    /// <summary><c>error</c>, <c>skip</c>, <c>replace</c> or <c>keep-both</c>.</summary>
    [JsonPropertyName("conflictAction")] public string ConflictAction { get; set; } = "error";

    /// <summary>Names this transfer in its progress events and in a <c>cancel</c>.</summary>
    [JsonPropertyName("operationId")] public string OperationId { get; set; } = "";
}

public sealed class RemoteTransferItem
{
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("destination")] public string Destination { get; set; } = "";
    [JsonPropertyName("is_dir")] public bool IsDir { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("bytes")] public ulong Bytes { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class RemoteTransferOutcome
{
    [JsonPropertyName("items")] public RemoteTransferItem[] Items { get; set; } = [];
    [JsonPropertyName("retry_sources")] public string[] RetrySources { get; set; } = [];

    /// <summary>A cancelled transfer replies normally with this set, not with an error.</summary>
    [JsonPropertyName("cancelled")] public bool Cancelled { get; set; }
}

/// <summary>The <c>remote.transfer-progress</c> event (camelCase).</summary>
public sealed class RemoteTransferProgress
{
    [JsonPropertyName("operationId")] public string OperationId { get; set; } = "";
    [JsonPropertyName("current")] public ulong Current { get; set; }
    [JsonPropertyName("total")] public ulong Total { get; set; }
    [JsonPropertyName("currentFiles")] public ulong CurrentFiles { get; set; }
    [JsonPropertyName("totalFiles")] public ulong TotalFiles { get; set; }
    [JsonPropertyName("currentItem")] public string CurrentItem { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
}

/// <summary>The <c>remote.transfer-tuning</c> event (camelCase).</summary>
public sealed class RemoteTransferTuning
{
    [JsonPropertyName("operationId")] public string OperationId { get; set; } = "";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("explanation")] public string Explanation { get; set; } = "";
}
