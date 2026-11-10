namespace SimpleFile.PluginSdk;

/// <summary>
/// The host services a plugin receives in <see cref="IPlugin.ActivateAsync"/> and keeps for its lifetime. The
/// services are callable from any thread unless a member says otherwise; the thread each plugin call runs on is listed
/// on <see cref="IPlugin"/>.
/// </summary>
/// <remarks>
/// A plugin that is disabled and enabled again gets a new <see cref="IPluginHost"/>. The old one stays usable as an
/// object, but its windows close at once and its backend calls start no backend.
/// </remarks>
public interface IPluginHost
{
    /// <summary>The plugin's id, as its manifest declares it, such as <c>com.example.hello</c>.</summary>
    string PluginId { get; }

    /// <summary>
    /// Calls <paramref name="method"/> on the plugin's backend and returns its result. Callable from any thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first call starts the backend when it is not running (a <c>first-call</c> backend, or one that stopped). The
    /// host then waits at most 5 s for the backend to connect to its pipe and 5 s more for the handshake; past either,
    /// or when the backend exits, the call fails. Three failed starts within 60 s mark the plugin Faulted.
    /// </para>
    /// <para>
    /// <paramref name="args"/> is serialized as the request's JSON <c>params</c>, and the result's JSON is deserialized as
    /// <typeparamref name="T"/>, with System.Text.Json: property names as declared (use <c>JsonPropertyName</c> for
    /// another spelling), matched case-sensitively, and null properties left out. A backend error fails the call with
    /// an exception whose message is the backend's.
    /// </para>
    /// <para>
    /// The host sets no deadline on the call itself. Cancelling <paramref name="ct"/> only ends this wait: no
    /// <c>cancel</c> reaches the backend, so a long backend operation takes an operation id the plugin cancels through
    /// its own method.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The type the result deserializes to.</typeparam>
    /// <param name="method">The backend method, named without a prefix; the plugin owns its method namespace.</param>
    /// <param name="args">The params object, or null for none.</param>
    /// <param name="ct">Ends the wait for the result.</param>
    /// <returns>The deserialized result.</returns>
    Task<T> InvokeBackendAsync<T>(string method, object? args, CancellationToken ct);

    /// <summary>
    /// Subscribes <paramref name="handler"/> to the backend's <paramref name="eventName"/> notifications; dispose the
    /// result to unsubscribe. Callable from any thread, also before the backend starts.
    /// </summary>
    /// <remarks>
    /// The handler runs on the connection's receive thread, not on the UI thread: marshal to the UI thread before it
    /// touches WinUI objects, and keep it short, because notifications are handled one at a time. An exception it throws
    /// is swallowed. The subscription carries over when the backend restarts.
    /// </remarks>
    /// <typeparam name="T">The type the notification's <c>params</c> deserialize to.</typeparam>
    /// <param name="eventName">The notification's method name.</param>
    /// <param name="handler">Called with each notification's params.</param>
    /// <returns>A handle that ends the subscription when disposed.</returns>
    IDisposable OnBackendEvent<T>(string eventName, Action<T> handler);

    /// <summary>The plugin's own settings.</summary>
    IPluginSettings Settings { get; }

    /// <summary>The plugin's rows in the Transfers window.</summary>
    IPluginProgress Progress { get; }

    /// <summary>Dialogs and message-bar notices shown through the host.</summary>
    IPluginDialogs Dialogs { get; }

    /// <summary>A read-only view of the active pane, and navigation requests.</summary>
    IPluginWorkspace Workspace { get; }

    /// <summary>The plugin log.</summary>
    IPluginLogger Logger { get; }

    /// <summary>Hands plugin-created windows to the host, which closes them before it stops the plugin.</summary>
    IPluginWindows Windows { get; }

    /// <summary>Shows and hides the plugin's <c>contributions.panels</c>; callable from any thread.</summary>
    IPluginPanels Panels { get; }

    /// <summary>Asks the plugin's row decoration provider again; callable from any thread.</summary>
    IPluginDecorations Decorations { get; }
}
