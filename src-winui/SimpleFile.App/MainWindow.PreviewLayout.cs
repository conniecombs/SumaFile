using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private bool _previewManuallyExpanded;
    private bool _previewEmptySettled = true;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _previewEmptyTimer;

    private void InitializePreviewLayout()
    {
        PreviewPanel.PinPreview += async (_, _) =>
        {
            if (_workspace is null) return;
            _workspace.Settings.PreviewPinned = PreviewPanel.Pinned;
            _previewManuallyExpanded = false;
            ApplyPreviewVisibility();
            await RunUiActionAsync("Pin preview", () => _workspace.SaveUiSettingsAsync());
        };
        Closed += (_, _) => _previewEmptyTimer?.Stop();
    }

    private bool PreviewHasContent()
    {
        if (ActiveSelectedRow is not null)
        {
            _previewEmptySettled = false;
            _previewEmptyTimer?.Stop();
            return true;
        }
        // Selection can briefly clear while navigation or a listing is settling.
        if (_workspace?.Active.ListingInProgress == true) return !_previewEmptySettled;
        if (!_previewEmptySettled)
        {
            if (_previewEmptyTimer is null)
            {
                _previewEmptyTimer = DispatcherQueue.CreateTimer();
                _previewEmptyTimer.Interval = TimeSpan.FromMilliseconds(400);
                _previewEmptyTimer.Tick += (_, _) =>
                {
                    _previewEmptyTimer.Stop();
                    _previewEmptySettled = ActiveSelectedRow is null;
                    ApplyPreviewVisibility();
                };
            }
            if (!_previewEmptyTimer.IsRunning) _previewEmptyTimer.Start();
            return true;
        }
        return false;
    }

    private void OnExplorerContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_workspace is null || Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 1) return;
        _previewManuallyExpanded = false;
        ApplyPreviewVisibility();
    }

    private void OnExpandPreview(object sender, RoutedEventArgs e)
    {
        _previewManuallyExpanded = true;
        ApplyPreviewVisibility();
    }
}
