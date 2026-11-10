namespace SimpleFile.PluginSdk;

/// <summary>
/// Shows and hides the plugin's own <c>contributions.panels</c>, reached through <see cref="IPluginHost.Panels"/>.
/// <c>panelId</c> is a panel's <c>id</c> in the manifest.
/// </summary>
/// <remarks>
/// Callable from any thread. <see cref="ShowAsync"/> and <see cref="Hide"/> are queued to the UI thread and never run
/// inline, so a plugin calling them from a command, a provider or a decoration request never re-enters a host pass.
/// </remarks>
public interface IPluginPanels
{
    /// <summary>
    /// Shows the panel docked and hides any other plugin panel; a panel the user already moved into its own window
    /// stays there and its window is activated. A hidden panel always shows docked: closing its window hides it.
    /// Completes once the panel is shown. A host without panels throws <see cref="InvalidOperationException"/>.
    /// </summary>
    Task ShowAsync(string panelId, CancellationToken ct);

    /// <summary>Hides the panel if it is shown; returns at once.</summary>
    void Hide(string panelId);

    /// <summary>Whether the panel is shown, as the UI thread last published it. False on a host without panels.</summary>
    bool IsVisible(string panelId);
}
