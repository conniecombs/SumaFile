using System.Globalization;
using SimpleFile.PluginSdk;

namespace SumaFile.Remote.Model;

/// <summary>
/// The eight plugin transfer settings the backend reads (<c>plugin.com.sumafile.remote.&lt;key&gt;</c>), with the
/// engine's defaults. Values are read and written as the backend parses them: integers in decimal, booleans as
/// <c>true</c>/<c>false</c>, the check mode by name.
/// </summary>
public sealed class RemoteTransferSettings
{
    public const string BandwidthMibKey = "bandwidthMib";
    public const string CheckModeKey = "checkMode";
    public const string InplaceKey = "inplace";
    public const string PreallocateKey = "preallocate";
    public const string FlushOnCloseKey = "flushOnClose";
    public const string LowLevelRetriesKey = "lowLevelRetries";
    public const string HighLevelRetriesKey = "highLevelRetries";
    public const string RetrySleepMsKey = "retrySleepMs";

    /// <summary>Every key, in the backend's <c>TRANSFER_SETTING_KEYS</c> order.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        BandwidthMibKey,
        CheckModeKey,
        InplaceKey,
        PreallocateKey,
        FlushOnCloseKey,
        LowLevelRetriesKey,
        HighLevelRetriesKey,
        RetrySleepMsKey,
    ];

    /// <summary>The check modes the backend accepts, in the manifest's option order.</summary>
    public static IReadOnlyList<string> CheckModes { get; } = ["size_and_mod_time", "size_only", "checksum", "ignore_times"];

    /// <summary>Each key's default as its plugin setting string; the manifest declares the same defaults.</summary>
    public static IReadOnlyDictionary<string, string> DefaultValues { get; } = new RemoteTransferSettings().ToSettingValues();

    // The setting strings this instance was loaded with (the defaults for a new instance); SaveAsync writes
    // only the keys whose value differs from them.
    private Dictionary<string, string> _baseline;

    public RemoteTransferSettings()
    {
        _baseline = new Dictionary<string, string>(ToSettingValues(), StringComparer.Ordinal);
    }

    /// <summary>MB/s; 0 is unlimited.</summary>
    public int BandwidthMib { get; set; }

    /// <summary>One of <see cref="CheckModes"/>.</summary>
    public string CheckMode { get; set; } = "size_and_mod_time";

    public bool Inplace { get; set; }
    public bool Preallocate { get; set; } = true;
    public bool FlushOnClose { get; set; } = true;
    public int LowLevelRetries { get; set; } = 10;
    public int HighLevelRetries { get; set; } = 3;
    public int RetrySleepMs { get; set; } = 1000;

    /// <summary>Reads every key; a missing or unparsable value keeps the default, as the backend does.</summary>
    public static async Task<RemoteTransferSettings> LoadAsync(IPluginSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var result = new RemoteTransferSettings();
        if (ParseInt(await settings.GetAsync(BandwidthMibKey, ct).ConfigureAwait(false)) is { } bandwidth)
            result.BandwidthMib = bandwidth;
        if (await settings.GetAsync(CheckModeKey, ct).ConfigureAwait(false) is { } mode
            && CheckModes.Contains(mode.Trim(), StringComparer.Ordinal))
            result.CheckMode = mode.Trim();
        if (ParseBool(await settings.GetAsync(InplaceKey, ct).ConfigureAwait(false)) is { } inplace)
            result.Inplace = inplace;
        if (ParseBool(await settings.GetAsync(PreallocateKey, ct).ConfigureAwait(false)) is { } preallocate)
            result.Preallocate = preallocate;
        if (ParseBool(await settings.GetAsync(FlushOnCloseKey, ct).ConfigureAwait(false)) is { } flush)
            result.FlushOnClose = flush;
        if (ParseInt(await settings.GetAsync(LowLevelRetriesKey, ct).ConfigureAwait(false)) is { } low)
            result.LowLevelRetries = low;
        if (ParseInt(await settings.GetAsync(HighLevelRetriesKey, ct).ConfigureAwait(false)) is { } high)
            result.HighLevelRetries = high;
        if (ParseInt(await settings.GetAsync(RetrySleepMsKey, ct).ConfigureAwait(false)) is { } sleep)
            result.RetrySleepMs = sleep;
        result._baseline = new Dictionary<string, string>(result.ToSettingValues(), StringComparer.Ordinal);
        return result;
    }

    /// <summary>Writes the keys whose value changed since this instance was loaded (or created).</summary>
    public async Task SaveAsync(IPluginSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var values = ToSettingValues();
        foreach (var key in Keys)
        {
            var value = values[key];
            if (!string.Equals(value, _baseline[key], StringComparison.Ordinal))
            {
                await settings.SetAsync(key, value, ct).ConfigureAwait(false);
                _baseline[key] = value;
            }
        }
    }

    /// <summary>Every key's plugin setting string, in <see cref="Keys"/> order.</summary>
    public IReadOnlyDictionary<string, string> ToSettingValues() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [BandwidthMibKey] = BandwidthMib.ToString(CultureInfo.InvariantCulture),
        [CheckModeKey] = CheckMode,
        [InplaceKey] = Bool(Inplace),
        [PreallocateKey] = Bool(Preallocate),
        [FlushOnCloseKey] = Bool(FlushOnClose),
        [LowLevelRetriesKey] = LowLevelRetries.ToString(CultureInfo.InvariantCulture),
        [HighLevelRetriesKey] = HighLevelRetries.ToString(CultureInfo.InvariantCulture),
        [RetrySleepMsKey] = RetrySleepMs.ToString(CultureInfo.InvariantCulture),
    };

    private static string Bool(bool value) => value ? "true" : "false";

    private static int? ParseInt(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static bool? ParseBool(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };
}
