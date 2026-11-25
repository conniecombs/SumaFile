using Microsoft.UI.Windowing;
using SimpleFile.Core;
using Windows.Graphics;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    /// <summary>The window's bounds the last time it was neither maximized nor minimized.</summary>
    private ScreenRect? _restoredBounds;
    private bool _windowMaximized;

    /// <summary>
    /// Opens the window where it was closed. Runs in the constructor, after the default size is
    /// set and before the window is shown, so there is no visible jump. A UI smoke run keeps the
    /// default size: its checks measure a window of that size.
    /// </summary>
    private void RestoreWindowPlacement()
    {
        // The smoke harness is in Debug builds only.
#if DEBUG
        if (UiSmokeRoot() is not null)
        {
            return;
        }
#endif

        try
        {
            if (WindowPlacementStore.Load(WindowPlacementStore.DefaultPath()) is { } saved)
            {
                // The screen the window was on, or the nearest one if that screen is gone.
                var bounds = saved.Bounds;
                var display = DisplayArea.GetFromRect(
                    new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                    DisplayAreaFallback.Nearest);
                var work = display.WorkArea;
                var fitted = saved.FitTo(new ScreenRect(work.X, work.Y, work.Width, work.Height));
                AppWindow.MoveAndResize(new RectInt32(fitted.X, fitted.Y, fitted.Width, fitted.Height));
                _restoredBounds = fitted;
                if (saved.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Maximize();
                    _windowMaximized = true;
                }
            }
        }
        catch (Exception)
        {
            // A placement that cannot be applied leaves the default size and position.
        }

        AppWindow.Changed += OnAppWindowPlacementChanged;
    }

    private void OnAppWindowPlacementChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange && !args.DidPresenterChange)
        {
            return;
        }

        if (sender.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        switch (presenter.State)
        {
            case OverlappedPresenterState.Restored:
                var position = sender.Position;
                var size = sender.Size;
                _restoredBounds = new ScreenRect(position.X, position.Y, size.Width, size.Height);
                _windowMaximized = false;
                break;
            case OverlappedPresenterState.Maximized:
                _windowMaximized = true;
                break;
            default:
                // Minimized: the bounds and the maximized state before it are what to come back to.
                break;
        }
    }

    /// <summary>Remembers the placement for the next start. Called once, when the window closes.</summary>
    private void SaveWindowPlacement()
    {
#if DEBUG
        if (UiSmokeRoot() is not null)
        {
            return;
        }
#endif

        try
        {
            AppWindow.Changed -= OnAppWindowPlacementChanged;
            if (_restoredBounds is not { } bounds)
            {
                // Never moved, resized or restored in this session: keep what was saved before.
                return;
            }

            WindowPlacementStore.Save(WindowPlacementStore.DefaultPath(), new WindowPlacement(bounds, _windowMaximized));
        }
        catch (Exception)
        {
            // Closing goes on; the window opens at its last saved or default placement next time.
        }
    }
}
