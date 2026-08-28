using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleFile.Ipc;

public sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = Protocol.JsonRpc;

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("method")]
    public required string Method { get; set; }

    [JsonPropertyName("params")]
    public object? Params { get; set; }
}

public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = Protocol.JsonRpc;

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }
}

public sealed class JsonRpcNotification
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = Protocol.JsonRpc;

    [JsonPropertyName("method")]
    public required string Method { get; set; }

    [JsonPropertyName("params")]
    public object? Params { get; set; }
}

public sealed class HandshakeParams
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = Protocol.Version;

    [JsonPropertyName("clientName")]
    public string ClientName { get; set; } = Protocol.ClientName;

    [JsonPropertyName("authToken")]
    public string AuthToken { get; set; } = "";

    [JsonPropertyName("binaryHotFrames")]
    public bool BinaryHotFrames { get; set; } = true;
}

public sealed class HandshakeResult
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = "";

    [JsonPropertyName("identifier")]
    public string Identifier { get; set; } = "";

    [JsonPropertyName("methodCount")]
    public int MethodCount { get; set; }

    [JsonPropertyName("binaryHotFrames")]
    public bool BinaryHotFrames { get; set; }

    [JsonPropertyName("binaryFrameVersion")]
    public int BinaryFrameVersion { get; set; }
}

public sealed class HealthResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = "";
}

public sealed class PathParams
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }
}

public sealed class ListDirectoryParams
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("finalEntries")]
    public bool? FinalEntries { get; init; }

    [JsonPropertyName("sortBy")]
    public string? SortBy { get; init; }

    [JsonPropertyName("sortAscending")]
    public bool? SortAscending { get; init; }

    [JsonPropertyName("filter")]
    public string? Filter { get; init; }

    [JsonPropertyName("includeHidden")]
    public bool? IncludeHidden { get; init; }
}

public sealed class SelectDirectoryParams
{
    [JsonPropertyName("defaultPath")]
    public string? DefaultPath { get; init; }
}
