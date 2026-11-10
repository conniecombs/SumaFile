using SimpleFile.PluginSdk;

namespace SumaFile.Git.Model;

/// <summary>The plugin's one declarative setting, <c>enabled</c> (plugin.json), which replaces 1.0.4's <c>EnableGitIntegration</c>.</summary>
public static class GitSettings
{
    public const string EnabledKey = "enabled";

    /// <summary>
    /// Whether Git integration is on. A missing or unparsable value reads as true, the manifest default; so does a
    /// settings store that fails to answer, which the host logs. Cancellation propagates.
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
            return true;
        }

        return !bool.TryParse(value?.Trim(), out var enabled) || enabled;
    }
}
