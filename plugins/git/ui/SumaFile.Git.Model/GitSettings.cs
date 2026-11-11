using System.Runtime.CompilerServices;
using SimpleFile.PluginSdk;

namespace SumaFile.Git.Model;

/// <summary>The plugin's one declarative setting, <c>enabled</c> (plugin.json), which replaces 1.0.4's <c>EnableGitIntegration</c>.</summary>
public static class GitSettings
{
    public const string EnabledKey = "enabled";

    // The last value each settings store answered, so a store that fails to answer keeps the user's choice.
    private static readonly ConditionalWeakTable<IPluginSettings, StrongBox<bool>> LastRead = new();

    /// <summary>
    /// Whether Git integration is on. A missing or unparsable value reads as true, the manifest default. A settings
    /// store that fails to answer (the host logs it) reads as the last value read from that store, so Git stays off
    /// for a user who turned it off while the settings service restarts; before any successful read it is true.
    /// Cancellation propagates.
    /// </summary>
    public static async Task<bool> IsEnabledAsync(IPluginSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? value;
        try
        {
            value = await settings.GetAsync(EnabledKey, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return !LastRead.TryGetValue(settings, out var last) || Volatile.Read(ref last.Value);
        }

        var enabled = !bool.TryParse(value?.Trim(), out var parsed) || parsed;
        Volatile.Write(ref LastRead.GetValue(settings, static _ => new StrongBox<bool>()).Value, enabled);
        return enabled;
    }
}
