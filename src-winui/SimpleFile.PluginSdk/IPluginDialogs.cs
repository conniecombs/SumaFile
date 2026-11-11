namespace SimpleFile.PluginSdk;

/// <summary>
/// Dialogs shown through the host, which queues them behind any other open dialog. Callable from any thread; the
/// host marshals to the UI thread.
/// </summary>
public interface IPluginDialogs
{
    /// <summary>Shows a message with an OK button over the main window.</summary>
    /// <param name="title">The dialog's title.</param>
    /// <param name="message">The dialog's text.</param>
    /// <param name="ct">Hides the dialog, or drops it while it is still queued; the task is then cancelled.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    Task ShowMessageAsync(string title, string message, CancellationToken ct);

    /// <summary>Asks the user to confirm, with <paramref name="confirmLabel"/> and Cancel buttons.</summary>
    /// <param name="title">The dialog's title.</param>
    /// <param name="message">The question.</param>
    /// <param name="confirmLabel">The confirm button's text, such as <c>Delete</c>.</param>
    /// <param name="ct">Hides the dialog, or drops it while it is still queued; the task is then cancelled.</param>
    /// <returns>True when the user confirmed; false for Cancel or Escape.</returns>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel, CancellationToken ct);

    /// <summary>Shows the folder picker.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The chosen folder's full path, or null when the user cancelled.</returns>
    Task<string?> PickFolderAsync(CancellationToken ct);

    /// <summary>
    /// Shows a dialog the plugin built. The SDK is WinUI-free, so the dialog crosses as <see cref="object"/>: pass a
    /// <c>Microsoft.UI.Xaml.Controls.ContentDialog</c>; anything else throws <see cref="ArgumentException"/>. A null
    /// <c>XamlRoot</c> means the main window; set it to a plugin window's <c>Content.XamlRoot</c> to show the dialog
    /// there. Create the dialog on the UI thread.
    /// </summary>
    /// <param name="contentDialog">The <c>ContentDialog</c> to show.</param>
    /// <param name="ct">Hides the dialog, or drops it while it is still queued; the task is then cancelled.</param>
    /// <returns>Which button closed the dialog.</returns>
    Task<PluginDialogResult> ShowDialogAsync(object contentDialog, CancellationToken ct);

    /// <summary>
    /// Shows <paramref name="message"/> in SumaFile's message bar, titled with the plugin's name, as an error when
    /// <paramref name="isError"/> is true and as information otherwise. It is not modal and does not wait: callable
    /// from any thread, it queues the message to the UI thread and returns at once.
    /// </summary>
    /// <param name="message">The text to show.</param>
    /// <param name="isError">True for an error, false for information.</param>
    void Notify(string message, bool isError);
}
