namespace SimpleFile.Core;

public interface ISettingsBackend
{
    Task<string?> GetSettingAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string?>> GetSettingsAsync(IReadOnlyList<string> keys, CancellationToken ct = default);
    Task SetSettingAsync(string key, string value, CancellationToken ct = default);
}
