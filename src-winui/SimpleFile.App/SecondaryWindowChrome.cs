using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;

namespace SimpleFile.App;

/// <summary>
/// One place for secondary-window setup: title, icon, backdrop, presenter flags, a DPI-aware
/// initial size (AppWindow.Resize takes physical pixels) and the app theme, which is pushed to
/// every live window when the user changes it. Windows that pass <c>cancel</c> / <c>primary</c>
/// also get Escape and Ctrl+Enter accelerators on their content root.
/// </summary>
internal static class SecondaryWindowChrome
{
    private static readonly List<WeakReference<Window>> LiveWindows = [];
    private static ElementTheme _theme = ElementTheme.Default;

    public static void Apply(
        Window window,
        string? title,
        int logicalWidth,
        int logicalHeight,
        bool mica = true,
        Action? cancel = null,
        Action? primary = null)
    {
        if (title is not null)
        {
            window.Title = title;
        }

        AppIcon.ApplyTo(window);
        if (mica)
        {
            window.SystemBackdrop = new MicaBackdrop();
        }

        Resize(window, logicalWidth, logicalHeight);
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        ApplyThemeTo(window, _theme);
        RegisterAccelerators(window, cancel, primary);
        lock (LiveWindows)
        {
            LiveWindows.RemoveAll(reference => !reference.TryGetTarget(out _));
            LiveWindows.Add(new WeakReference<Window>(window));
        }

        window.Closed += (_, _) =>
        {
            lock (LiveWindows)
            {
                LiveWindows.RemoveAll(reference => !reference.TryGetTarget(out var live) || ReferenceEquals(live, window));
            }
        };
    }

    /// <summary>Escape runs <paramref name="cancel"/>; Ctrl+Enter runs <paramref name="primary"/>.</summary>
    private static void RegisterAccelerators(Window window, Action? cancel, Action? primary)
    {
        if (window.Content is not UIElement root || (cancel is null && primary is null))
        {
            return;
        }

        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        if (cancel is not null)
        {
            AddAccelerator(root, VirtualKey.Escape, VirtualKeyModifiers.None, cancel);
        }

        if (primary is not null)
        {
            AddAccelerator(root, VirtualKey.Enter, VirtualKeyModifiers.Control, primary);
        }
    }

    private static void AddAccelerator(UIElement root, VirtualKey key, VirtualKeyModifiers modifiers, Action invoke)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            invoke();
        };
        root.KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>Resizes to a size given in device-independent pixels.</summary>
    public static void Resize(Window window, int logicalWidth, int logicalHeight)
    {
        var scale = ScaleFor(window);
        window.AppWindow.Resize(new SizeInt32(
            (int)Math.Round(logicalWidth * scale),
            (int)Math.Round(logicalHeight * scale)));
    }

    public static double ScaleFor(Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0)
            {
                return dpi / 96.0;
            }
        }
        catch
        {
            // Fall through to the unscaled size.
        }

        return 1.0;
    }

    /// <summary>Applies the app theme now and to every window opened through <see cref="Apply"/> later.</summary>
    public static void ApplyTheme(ElementTheme theme)
    {
        _theme = theme;
        Window[] live;
        lock (LiveWindows)
        {
            live = LiveWindows
                .Select(reference => reference.TryGetTarget(out var window) ? window : null)
                .OfType<Window>()
                .ToArray();
        }

        foreach (var window in live)
        {
            ApplyThemeTo(window, theme);
        }
    }

    private static void ApplyThemeTo(Window window, ElementTheme theme)
    {
        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
