using Microsoft.Web.WebView2.Core;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

internal sealed partial class PreviewPresenter
{
    private bool _previewWebViewConfigured;
    private Uri? _allowedPreviewNavigation;

    /// <summary>
    /// Initialises the embedded WebView2 with a locked-down profile: no DevTools, context menus,
    /// host objects, web messages or script dialogs; script only where the built-in PDF viewer
    /// needs it; and no navigation away from the content the presenter itself loads. External
    /// http(s) links are handed to the operating system instead.
    /// </summary>
    private async Task EnsurePreviewWebViewAsync(bool allowScript, Uri? allowedNavigation)
    {
        await _pdfView.EnsureCoreWebView2Async();
        var core = _pdfView.CoreWebView2;
        if (!_previewWebViewConfigured)
        {
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += OnPreviewWebViewNavigationStarting;
            core.NewWindowRequested += OnPreviewWebViewNewWindowRequested;
            _previewWebViewConfigured = true;
        }

        core.Settings.IsScriptEnabled = allowScript;
        _allowedPreviewNavigation = allowedNavigation;
    }

    private void OnPreviewWebViewNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (IsAllowedPreviewNavigation(args.Uri))
        {
            return;
        }

        args.Cancel = true;
        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    private static void OnPreviewWebViewNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    private bool IsAllowedPreviewNavigation(string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return false;
        }

        if (_allowedPreviewNavigation is { } allowed)
        {
            return Uri.TryCreate(target, UriKind.Absolute, out var uri)
                && uri.IsFile
                && allowed.IsFile
                && string.Equals(uri.LocalPath, allowed.LocalPath, StringComparison.OrdinalIgnoreCase);
        }

        // NavigateToString content is reported as about:blank or an inline data: document.
        return target.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryRenderHtmlPreviewAsync(
        string path,
        FilePreview preview,
        int token,
        CancellationToken cancellationToken)
    {
        if (!PreviewCapabilities.CanRenderAsHtml(path, preview) || preview.Content is null)
        {
            return false;
        }

        if (!IsCurrent(path, token, cancellationToken))
        {
            return true;
        }

        try
        {
            ClearIcon();
            _textBox.Text = "";
            _textBox.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            _mediaPlayer.Source = null;
            _mediaPlayer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            HideVideoFrameControls();
            _pdfView.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            await EnsurePreviewWebViewAsync(allowScript: false, allowedNavigation: null);
            if (!IsCurrent(path, token, cancellationToken))
            {
                return true;
            }

            _pdfView.NavigateToString(PreviewHtmlRenderer.RenderDocument(path, preview));
            _emptyText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            return true;
        }
        catch
        {
            _pdfView.Source = null;
            _pdfView.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            return false;
        }
    }
}
