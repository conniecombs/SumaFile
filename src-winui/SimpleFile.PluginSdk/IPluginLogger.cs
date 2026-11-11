namespace SimpleFile.PluginSdk;

/// <summary>
/// Writes to SumaFile's plugin log (<c>plugins.log</c> next to the app), each line tagged with the plugin id and a
/// level. Callable from any thread; it never throws for a log it cannot write.
/// </summary>
public interface IPluginLogger
{
    /// <summary>Writes an <c>[info]</c> line.</summary>
    /// <param name="message">The text of the line.</param>
    void Info(string message);

    /// <summary>Writes a <c>[warn]</c> line.</summary>
    /// <param name="message">The text of the line.</param>
    void Warn(string message);

    /// <summary>Writes an <c>[error]</c> line, followed by <paramref name="exception"/> when one is given.</summary>
    /// <param name="message">The text of the line.</param>
    /// <param name="exception">The exception to append, with its stack trace; null for none.</param>
    void Error(string message, Exception? exception = null);
}
