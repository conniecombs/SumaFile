using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SumaFile.Remote.Model;
using Windows.System;

namespace SumaFile.Remote.Views;

/// <summary>
/// A grip in the gap between two of the manager's panels. Dragging it reports the pointer's position across
/// <c>relativeTo</c>; it is also a tab stop, where Left and Right move it by <see cref="RemotePaneLayout.KeyStep"/>
/// and Home resets it, as a double-click does. The window turns what the grip reports into column widths. A control
/// subclass, so it can take the keyboard focus and set the protected <see cref="UIElement.ProtectedCursor"/>.
/// </summary>
internal sealed class RemotePaneSplitter : ContentControl
{
    private readonly UIElement _relativeTo;
    private bool _dragging;

    public RemotePaneSplitter(string automationId, string name, UIElement relativeTo)
    {
        _relativeTo = relativeTo ?? throw new ArgumentNullException(nameof(relativeTo));
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetAutomationId(this, automationId);
        ToolTipService.SetToolTip(this, "Drag to resize, or press the Left and Right arrow keys. Double-click or press Home to reset.");
        // A transparent fill, so the whole gap takes the pointer.
        Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };

        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => _dragging = false;
        DoubleTapped += (_, e) =>
        {
            Reset?.Invoke();
            e.Handled = true;
        };
        KeyDown += OnKey;
    }

    /// <summary>The pointer's horizontal position across the element given to the constructor, while dragging.</summary>
    public Action<double>? Dragged { get; set; }

    /// <summary>An arrow key: the distance to move by, negative to the left.</summary>
    public Action<double>? Nudged { get; set; }

    /// <summary>A double-click or Home.</summary>
    public Action? Reset { get; set; }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        Dragged?.Invoke(e.GetCurrentPoint(_relativeTo).Position.X);
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Left:
                Nudged?.Invoke(-RemotePaneLayout.KeyStep);
                break;
            case VirtualKey.Right:
                Nudged?.Invoke(RemotePaneLayout.KeyStep);
                break;
            case VirtualKey.Home:
                Reset?.Invoke();
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
