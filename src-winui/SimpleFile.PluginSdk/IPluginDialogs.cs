namespace SimpleFile.PluginSdk;

/// <summary>
/// Dialogs shown through the host, which queues them behind any other open dialog. Callable from any thread; the
/// host marshals to the UI thread.
/// </summary>
public interface IPluginDialogs
{
    Task ShowMessageAsync(string title, string message, CancellationToken ct);
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel, CancellationToken ct);
    Task<string?> PickFolderAsync(CancellationToken ct);

    /// <summary>
    /// Shows a dialog the plugin built. The SDK is WinUI-free, so the dialog crosses as <see cref="object"/>: pass a
    /// <c>Microsoft.UI.Xaml.Controls.ContentDialog</c>; anything else throws <see cref="ArgumentException"/>. A null
    /// <c>XamlRoot</c> means the main window; set it to a plugin window's <c>Content.XamlRoot</c> to show the dialog
    /// there. Create the dialog on the UI thread.
    /// </summary>
    Task<PluginDialogResult> ShowDialogAsync(object contentDialog, CancellationToken ct);
}
