namespace SimpleFile.PluginSdk;

/// <summary>
/// The plugin's own settings, reached through <see cref="IPluginHost.Settings"/>: the same values as the declarative
/// <c>settings</c> of the manifest and the backend's <c>host.get_settings</c> and <c>host.set_setting</c>. Values are
/// strings. Callable from any thread.
/// </summary>
/// <remarks>
/// A key is 1 to 64 characters of letters, digits, <c>_</c> or <c>-</c>, starting with a lowercase letter or digit
/// (<c>^[a-z0-9][a-zA-Z0-9_-]{0,63}$</c>). Any other key throws <see cref="ArgumentException"/>. The host stores the
/// key under the plugin's own namespace, so a plugin can never read or write another plugin's settings.
/// </remarks>
public interface IPluginSettings
{
    /// <summary>Reads a setting.</summary>
    /// <param name="key">The setting's key.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The stored value, or null when none is stored.</returns>
    Task<string?> GetAsync(string key, CancellationToken ct);

    /// <summary>Writes a setting; Settings &gt; Plugins shows the new value the next time it is drawn.</summary>
    /// <param name="key">The setting's key.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>A task that completes once the value is stored.</returns>
    Task SetAsync(string key, string value, CancellationToken ct);
}
