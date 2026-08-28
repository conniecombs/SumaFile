using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleFile.Ipc;

public static class IpcJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
    };
}
