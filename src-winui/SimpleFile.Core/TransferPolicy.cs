using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleFile.Core;

public sealed record TransferPolicy
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "automatic";
    [JsonPropertyName("maxConcurrency")]
    public int MaxConcurrency { get; init; } = 16;
    [JsonPropertyName("manualConcurrency")]
    public int ManualConcurrency { get; init; } = 4;
    [JsonPropertyName("checkers")]
    public int Checkers { get; init; } = 8;
    [JsonPropertyName("maxBacklog")]
    public int MaxBacklog { get; init; } = 10_000;
    [JsonPropertyName("orderBy")]
    public string OrderBy { get; init; } = "default";
    [JsonPropertyName("bufferSizeKib")]
    public int BufferSizeKib { get; init; } = 4096;
    [JsonPropertyName("memoryBudgetMb")]
    public int MemoryBudgetMb { get; init; } = 192;
    [JsonPropertyName("multiThreadCutoffMib")]
    public int MultiThreadCutoffMib { get; init; } = 64;
    [JsonPropertyName("multiThreadStreams")]
    public int MultiThreadStreams { get; init; } = 4;
    [JsonPropertyName("checkMode")]
    public string CheckMode { get; init; } = "size_and_mod_time";
    [JsonPropertyName("inplace")]
    public bool Inplace { get; init; }
    [JsonPropertyName("preallocate")]
    public bool Preallocate { get; init; } = true;
    [JsonPropertyName("flushOnClose")]
    public bool FlushOnClose { get; init; } = true;
    [JsonPropertyName("bandwidthMib")]
    public int BandwidthMib { get; init; }
    [JsonPropertyName("lowLevelRetries")]
    public int LowLevelRetries { get; init; } = 10;
    [JsonPropertyName("highLevelRetries")]
    public int HighLevelRetries { get; init; } = 3;
    [JsonPropertyName("retrySleepMs")]
    public int RetrySleepMs { get; init; } = 1000;

    public TransferPolicy Normalize() => this with
    {
        Mode = Mode is "automatic" or "conservative" or "maximum" or "manual" ? Mode : "automatic",
        MaxConcurrency = Math.Clamp(MaxConcurrency, 1, 32),
        ManualConcurrency = Math.Clamp(ManualConcurrency, 1, 32),
        Checkers = Math.Clamp(Checkers, 1, 64),
        MaxBacklog = Math.Clamp(MaxBacklog, 128, 100_000),
        OrderBy = OrderBy is "default" or "size_asc" or "size_desc" or "name" ? OrderBy : "default",
        BufferSizeKib = Math.Clamp(BufferSizeKib, 64, 65_536),
        MemoryBudgetMb = Math.Clamp(MemoryBudgetMb, 16, 512),
        MultiThreadCutoffMib = Math.Clamp(MultiThreadCutoffMib, 1, 16_384),
        MultiThreadStreams = Math.Clamp(MultiThreadStreams, 0, 16),
        CheckMode = CheckMode is "size_and_mod_time" or "size_only" or "checksum" or "ignore_times" ? CheckMode : "size_and_mod_time",
        BandwidthMib = Math.Clamp(BandwidthMib, 0, 1_048_576),
        LowLevelRetries = Math.Clamp(LowLevelRetries, 1, 50),
        HighLevelRetries = Math.Clamp(HighLevelRetries, 1, 20),
        RetrySleepMs = Math.Clamp(RetrySleepMs, 10, 60_000),
    };

    public static TransferPolicy Parse(string? json)
    {
        try { return (JsonSerializer.Deserialize<TransferPolicy>(json ?? "{}") ?? new()).Normalize(); }
        catch (JsonException) { return new(); }
    }
}

public static class TransferPolicyStore
{
    public const string DefaultKey = "transfer.policy";
    private const string OverridesKey = "transfer.overrides.v1";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private sealed record Override(
        [property: JsonPropertyName("policy")] TransferPolicy Policy,
        [property: JsonPropertyName("updated")] long Updated);

    public static async Task<TransferPolicy> LoadAsync(ISettingsBackend backend, CancellationToken ct = default) =>
        TransferPolicy.Parse(await backend.GetSettingAsync(DefaultKey, ct).ConfigureAwait(false));

    public static Task SaveAsync(ISettingsBackend backend, TransferPolicy policy, CancellationToken ct = default) =>
        backend.SetSettingAsync(DefaultKey, JsonSerializer.Serialize(policy.Normalize()), ct);

    public static Task ClearLearningAsync(ISettingsBackend backend, CancellationToken ct = default) =>
        backend.SetSettingAsync("transfer.learning.v1", "{}", ct);

    public static async Task SetOverrideAsync(ISettingsBackend backend, string operationId,
        TransferPolicy? policy, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Dictionary<string, Override> values;
            try { values = JsonSerializer.Deserialize<Dictionary<string, Override>>(
                await backend.GetSettingAsync(OverridesKey, ct).ConfigureAwait(false) ?? "{}") ?? []; }
            catch (JsonException) { values = []; }
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            values = values.Where(pair => pair.Value is not null && pair.Value.Updated <= now && now - pair.Value.Updated < 86400)
                .OrderByDescending(pair => pair.Value.Updated).Take(31).ToDictionary(pair => pair.Key, pair => pair.Value);
            if (policy is null) values.Remove(operationId);
            else values[operationId] = new Override(policy.Normalize(), now);
            await backend.SetSettingAsync(OverridesKey, JsonSerializer.Serialize(values), ct).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }
}
