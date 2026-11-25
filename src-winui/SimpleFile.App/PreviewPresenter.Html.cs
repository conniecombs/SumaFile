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
            LockDownPreviewWebView(core);
            core.NavigationStarting += OnPreviewWebViewNavigationStarting;
            core.NewWindowRequested += OnPreviewWebViewNewWindowRequested;
            _previewWebViewConfigured = true;
        }

        core.Settings.IsScriptEnabled = allowScript;
        _allowedPreviewNavigation = allowedNavigation;
    }

    /// <summary>
    /// The settings every preview WebView2 gets, in the pane and in Quick Look: no DevTools,
    /// context menus, host objects, web messages, script dialogs or status bar.
    /// </summary>
    private static void LockDownPreviewWebView(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
    }

    private void OnPreviewWebViewNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (IsAllowedPreviewNavigation(args.Uri, _allowedPreviewNavigation))
        {
            return;
        }

        args.Cancel = true;
        OpenExternalLink(args.Uri);
    }

    private static void OnPreviewWebViewNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        OpenExternalLink(args.Uri);
    }

    /// <summary>Hands an http(s) link to the operating system; anything else is dropped.</summary>
    private static void OpenExternalLink(string? target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    /// <summary>
    /// Builds the Quick Look PDF view with the same lockdown as the pane's. The file is loaded
    /// only after the settings are in place, and <paramref name="cleanup"/> closes the view so
    /// the browser process lets go of the file.
    /// </summary>
    private static Microsoft.UI.Xaml.Controls.WebView2 CreateQuickLookPdfView(Uri source, double height, out Action cleanup)
    {
        var view = new Microsoft.UI.Xaml.Controls.WebView2
        {
            Height = height,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
        };
        var started = false;
        var closed = false;
        view.Loaded += async (_, _) =>
        {
            if (started || closed)
            {
                return;
            }

            started = true;
            try
            {
                await view.EnsureCoreWebView2Async();
                if (closed)
                {
                    return;
                }

                var core = view.CoreWebView2;
                LockDownPreviewWebView(core);
                // The built-in PDF viewer needs script; navigation stays pinned to this file.
                core.Settings.IsScriptEnabled = true;
                core.NavigationStarting += (_, args) =>
                {
                    if (!IsAllowedPreviewNavigation(args.Uri, source))
                    {
                        args.Cancel = true;
                        OpenExternalLink(args.Uri);
                    }
                };
                core.NewWindowRequested += OnPreviewWebViewNewWindowRequested;
                view.Source = source;
            }
            catch
            {
                // The viewer could not start, or Quick Look closed first; the dialog stays usable.
            }
        };
        cleanup = () =>
        {
            closed = true;
            try
            {
                // Close, not "Source = null": a WebView2 rejects a null source, and only closing
                // ends the browser process that holds the file open.
                view.Close();
            }
            catch
            {
                // A view that never started has nothing to close.
            }
        };
        return view;
    }

    /// <summary>
    /// Builds Quick Look's rendered view of a text file (Markdown, HTML, CSV, JSON...) with the lockdown of the
    /// pane's: no script at all, and no navigation away from the document handed to it; a link is given to the
    /// operating system. <paramref name="cleanup"/> closes the view.
    /// </summary>
    internal static Microsoft.UI.Xaml.Controls.WebView2 CreateQuickLookHtmlView(string html, double height, out Action cleanup)
    {
        var view = new Microsoft.UI.Xaml.Controls.WebView2
        {
            Height = height,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
        };
        var started = false;
        var closed = false;
        view.Loaded += async (_, _) =>
        {
            if (started || closed)
            {
                return;
            }

            started = true;
            try
            {
                await view.EnsureCoreWebView2Async();
                if (closed)
                {
                    return;
                }

                var core = view.CoreWebView2;
                LockDownPreviewWebView(core);
                core.Settings.IsScriptEnabled = false;
                core.NavigationStarting += (_, args) =>
                {
                    if (!IsAllowedPreviewNavigation(args.Uri, null))
                    {
                        args.Cancel = true;
                        OpenExternalLink(args.Uri);
                    }
                };
                core.NewWindowRequested += OnPreviewWebViewNewWindowRequested;
                view.NavigateToString(html);
            }
            catch
            {
                // The viewer could not start, or Quick Look closed first; the source view is still there.
            }
        };
        cleanup = () =>
        {
            closed = true;
            try
            {
                view.Close();
            }
            catch
            {
                // A view that never started has nothing to close.
            }
        };
        return view;
    }

    private static bool IsAllowedPreviewNavigation(string? target, Uri? allowedFile)
    {
        if (string.IsNullOrEmpty(target))
        {
            return false;
        }

        if (allowedFile is { } allowed)
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
            ReleaseMediaSource();
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
