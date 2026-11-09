using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.PluginSdk;

namespace SimpleFile.App;

/// <summary>
/// One plugin's dialogs, shown through <see cref="DialogHost"/> so they queue behind any other open
/// dialog. Each dialog names the plugin, so a plugin cannot pass its prompt off as SumaFile's own.
/// </summary>
internal sealed class PluginDialogs : IPluginDialogs
{
    private readonly string _pluginId;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<XamlRoot?> _xamlRoot;
    private readonly Func<string, string> _pluginName;
    private readonly Func<Task<string?>> _pickFolder;

    public PluginDialogs(
        string pluginId,
        DispatcherQueue dispatcher,
        Func<XamlRoot?> xamlRoot,
        Func<string, string> pluginName,
        Func<Task<string?>> pickFolder)
    {
        _pluginId = pluginId;
        _dispatcher = dispatcher;
        _xamlRoot = xamlRoot;
        _pluginName = pluginName;
        _pickFolder = pickFolder;
    }

    public Task ShowMessageAsync(string title, string message, CancellationToken ct) =>
        PluginUiDispatch.RunAsync(_dispatcher, async () =>
        {
            await ShowAsync(CreateDialog(title, message, closeLabel: "OK"), ct);
            return true;
        });

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, CancellationToken ct) =>
        PluginUiDispatch.RunAsync(_dispatcher, async () =>
        {
            var dialog = CreateDialog(title, message, closeLabel: "Cancel");
            dialog.PrimaryButtonText = string.IsNullOrWhiteSpace(confirmLabel) ? "OK" : confirmLabel;
            dialog.DefaultButton = ContentDialogButton.Primary;
            return await ShowAsync(dialog, ct) == ContentDialogResult.Primary;
        });

    public Task<string?> PickFolderAsync(CancellationToken ct) =>
        PluginUiDispatch.RunAsync(_dispatcher, async () =>
        {
            ct.ThrowIfCancellationRequested();
            return await _pickFolder();
        });

    /// <summary>
    /// Shows a dialog the plugin built through the same gate as SumaFile's own dialogs. A dialog without a
    /// <c>XamlRoot</c> opens on the main window; a plugin window sets its own.
    /// </summary>
    public Task<PluginDialogResult> ShowDialogAsync(object contentDialog, CancellationToken ct)
    {
        if (contentDialog is not ContentDialog dialog)
        {
            throw new ArgumentException("contentDialog must be a Microsoft.UI.Xaml.Controls.ContentDialog.", nameof(contentDialog));
        }

        return PluginUiDispatch.RunAsync(_dispatcher, async () =>
        {
            dialog.XamlRoot ??= _xamlRoot() ?? throw new InvalidOperationException("SumaFile's window is not ready for dialogs.");
            return ToPluginResult(await ShowAsync(dialog, ct));
        });
    }

    internal static PluginDialogResult ToPluginResult(ContentDialogResult result) => result switch
    {
        ContentDialogResult.Primary => PluginDialogResult.Primary,
        ContentDialogResult.Secondary => PluginDialogResult.Secondary,
        _ => PluginDialogResult.None,
    };

    private ContentDialog CreateDialog(string title, string message, string closeLabel)
    {
        var root = _xamlRoot() ?? throw new InvalidOperationException("SumaFile's window is not ready for dialogs.");
        return new ContentDialog
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 8,
                MaxWidth = 460,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                    new TextBlock
                    {
                        Text = $"From the {_pluginName(_pluginId)} plugin",
                        FontSize = 12,
                        Opacity = 0.72,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
            CloseButtonText = closeLabel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };
    }

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var registration = ct.Register(() => _dispatcher.TryEnqueue(dialog.Hide));
        var result = await DialogHost.ShowAsync(dialog, ct);
        ct.ThrowIfCancellationRequested();
        return result;
    }
}

/// <summary>Runs plugin-requested UI work on the window's thread and hands the result back to any thread.</summary>
internal static class PluginUiDispatch
{
    public static Task<T> RunAsync<T>(DispatcherQueue dispatcher, Func<Task<T>> work)
    {
        if (dispatcher.HasThreadAccess)
        {
            return work();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = dispatcher.TryEnqueue(async () =>
        {
            try
            {
                completion.TrySetResult(await work());
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        if (!queued)
        {
            completion.TrySetException(new InvalidOperationException("SumaFile's window has closed."));
        }

        return completion.Task;
    }
}
