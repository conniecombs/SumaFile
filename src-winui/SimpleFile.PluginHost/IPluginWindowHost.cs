using SimpleFile.PluginSdk;

namespace SimpleFile.PluginHost;

/// <summary>
/// One plugin runtime's windows on the host side: <see cref="IPluginWindows.Attach"/> for the plugin, and
/// <see cref="CloseAllAsync"/> for the manager, which awaits it (bounded by
/// <see cref="PluginManager.WindowCloseTimeout"/>) before it stops the backend and deactivates the plugin.
/// </summary>
/// <remarks>
/// A host serves one runtime. The manager asks <see cref="PluginManagerOptions.WindowsFor"/> for a new host whenever
/// it creates a runtime, and calls <see cref="CloseAllAsync"/> only on a runtime it has already retired, so a sealed
/// host is never handed to a re-enabled plugin. <see cref="PluginWindowSet{TWindow}"/> holds the bookkeeping and the
/// seal.
/// </remarks>
public interface IPluginWindowHost : IPluginWindows
{
    /// <summary>
    /// Seals the host, then closes every window attached so far; completes once they are closed.
    /// </summary>
    /// <remarks>
    /// The seal takes effect when this is called, before the returned task completes, and lasts for good: from then
    /// on <see cref="IPluginWindows.Attach"/> closes the window it is handed at once and tracks nothing. A plugin that
    /// opens a window while it is being stopped (an error window after its backend died, say) therefore cannot leave
    /// one open past <see cref="IPlugin.DeactivateAsync"/>. A window attached concurrently with this call is either
    /// closed with the rest or closed at once by <c>Attach</c>, never left open.
    /// </remarks>
    Task CloseAllAsync();
}

/// <summary>The default <see cref="IPluginWindowHost"/> when the App supplies none: it ignores <c>Attach</c>.</summary>
internal sealed class NullPluginWindowHost : IPluginWindowHost
{
    public static readonly NullPluginWindowHost Instance = new();

    public void Attach(object window, PluginWindowOptions options)
    {
    }

    public Task CloseAllAsync() => Task.CompletedTask;
}

/// <summary>
/// The host's check of a plugin's <see cref="PluginWindowOptions"/> before a window is tracked (parked #72/#92): the
/// size is clamped to <see cref="MinWidth"/>-<see cref="MaxWidth"/> by <see cref="MinHeight"/>-<see cref="MaxHeight"/>
/// device-independent pixels, a null, empty or whitespace title becomes the fallback (the plugin's manifest name), and
/// the title is cut to <see cref="MaxTitleLength"/> characters. The clamped size times any DPI scale Windows offers stays
/// far inside <see cref="int"/>, so the physical size cannot overflow.
/// </summary>
public static class PluginWindowOptionsPolicy
{
    /// <summary>The smallest initial width, in DIPs.</summary>
    public const int MinWidth = 320;

    /// <summary>The largest initial width, in DIPs.</summary>
    public const int MaxWidth = 3840;

    /// <summary>The smallest initial height, in DIPs.</summary>
    public const int MinHeight = 240;

    /// <summary>The largest initial height, in DIPs.</summary>
    public const int MaxHeight = 2160;

    /// <summary>The longest title, in UTF-16 characters.</summary>
    public const int MaxTitleLength = 120;

    /// <summary>
    /// <paramref name="options"/> with its size clamped and its title present and cut to length. Idempotent: normalized
    /// options come back equal. A cut never splits a surrogate pair.
    /// </summary>
    /// <param name="options">What the plugin passed to <see cref="IPluginWindows.Attach"/>.</param>
    /// <param name="fallbackTitle">The title for a blank one: the plugin's manifest name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="fallbackTitle"/> is null, empty or whitespace.</exception>
    public static PluginWindowOptions Normalize(PluginWindowOptions options, string fallbackTitle)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackTitle);
        var title = Cut(string.IsNullOrWhiteSpace(options.Title) ? fallbackTitle : options.Title);
        var width = Math.Clamp(options.Width, MinWidth, MaxWidth);
        var height = Math.Clamp(options.Height, MinHeight, MaxHeight);
        return title == options.Title && width == options.Width && height == options.Height
            ? options
            : new PluginWindowOptions(title, width, height);
    }

    private static string Cut(string title)
    {
        if (title.Length <= MaxTitleLength) return title;
        var length = char.IsHighSurrogate(title[MaxTitleLength - 1]) ? MaxTitleLength - 1 : MaxTitleLength;
        return title[..length];
    }
}

/// <summary>
/// What a plugin sees as <see cref="IPluginHost.Windows"/>: it normalizes the options with
/// <see cref="PluginWindowOptionsPolicy"/> and the plugin's manifest name before <see cref="Inner"/>, the runtime's one
/// window host, sees them, so every window host gets checked options. <see cref="CloseAllAsync"/> forwards unchanged.
/// </summary>
internal sealed class PluginWindowOptionsGuard(IPluginWindowHost inner, string fallbackTitle) : IPluginWindowHost
{
    /// <summary>The runtime's window host, from <see cref="PluginManagerOptions.WindowsFor"/>.</summary>
    public IPluginWindowHost Inner { get; } = inner;

    public void Attach(object window, PluginWindowOptions options) =>
        Inner.Attach(window, PluginWindowOptionsPolicy.Normalize(options, fallbackTitle));

    public Task CloseAllAsync() => Inner.CloseAllAsync();
}
