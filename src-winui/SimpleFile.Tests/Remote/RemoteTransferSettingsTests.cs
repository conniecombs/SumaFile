using SimpleFile.PluginSdk;
using SumaFile.Remote.Model;
using Xunit;

namespace SimpleFile.Tests.Remote;

public class RemoteTransferSettingsTests
{
    [Fact]
    public void DefaultValues_AreTheEngineDefaultsInKeyOrder()
    {
        Assert.Equal(
            ["bandwidthMib", "checkMode", "inplace", "preallocate", "flushOnClose", "lowLevelRetries", "highLevelRetries", "retrySleepMs"],
            RemoteTransferSettings.Keys);
        Assert.Equal(
            ["0", "size_and_mod_time", "false", "true", "true", "10", "3", "1000"],
            RemoteTransferSettings.Keys.Select(key => RemoteTransferSettings.DefaultValues[key]));
        Assert.Equal(RemoteTransferSettings.DefaultValues, new RemoteTransferSettings().ToSettingValues());
    }

    [Fact]
    public async Task Load_ReadsEveryKeyAndKeepsTheDefaultForAMissingOrUnparsableValue()
    {
        var store = new MemorySettings
        {
            ["bandwidthMib"] = "5",
            ["checkMode"] = "checksum",
            ["inplace"] = "TRUE",
            ["preallocate"] = "nope",
            ["flushOnClose"] = "false",
            ["lowLevelRetries"] = "4",
            ["highLevelRetries"] = "x",
            ["retrySleepMs"] = "250",
        };

        var settings = await RemoteTransferSettings.LoadAsync(store, CancellationToken.None);

        Assert.Equal(5, settings.BandwidthMib);
        Assert.Equal("checksum", settings.CheckMode);
        Assert.True(settings.Inplace);
        Assert.True(settings.Preallocate);
        Assert.False(settings.FlushOnClose);
        Assert.Equal(4, settings.LowLevelRetries);
        Assert.Equal(3, settings.HighLevelRetries);
        Assert.Equal(250, settings.RetrySleepMs);
        Assert.Equal(8, store.Reads);
        Assert.Equal("size_and_mod_time", (await RemoteTransferSettings.LoadAsync(
            new MemorySettings { ["checkMode"] = "fastest" }, CancellationToken.None)).CheckMode);
    }

    [Fact]
    public async Task Save_WritesOnlyChangedKeys()
    {
        var store = new MemorySettings { ["bandwidthMib"] = "5", ["preallocate"] = "garbage" };
        var settings = await RemoteTransferSettings.LoadAsync(store, CancellationToken.None);

        await settings.SaveAsync(store, CancellationToken.None);
        Assert.Empty(store.Writes);

        settings.BandwidthMib = 0;
        settings.CheckMode = "size_only";
        settings.RetrySleepMs = 1000;
        await settings.SaveAsync(store, CancellationToken.None);

        Assert.Equal([("bandwidthMib", "0"), ("checkMode", "size_only")], store.Writes);
        await settings.SaveAsync(store, CancellationToken.None);
        Assert.Equal(2, store.Writes.Count);
    }

    private sealed class MemorySettings : Dictionary<string, string>, IPluginSettings
    {
        public int Reads { get; private set; }
        public List<(string Key, string Value)> Writes { get; } = [];

        public Task<string?> GetAsync(string key, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(TryGetValue(key, out var value) ? value : null);
        }

        public Task SetAsync(string key, string value, CancellationToken ct)
        {
            Writes.Add((key, value));
            this[key] = value;
            return Task.CompletedTask;
        }
    }
}
