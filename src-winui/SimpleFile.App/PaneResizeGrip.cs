using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace SimpleFile.App;

/// <summary>
/// Vertical splitter hit target. Subclassed so <see cref="UIElement.ProtectedCursor"/>
/// can show the west-east resize pointer.
/// </summary>
public sealed class PaneResizeGrip : Grid
{
    public PaneResizeGrip()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        MinWidth = 8;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        PointerEntered += (_, _) => SetLine(hover: true);
        PointerExited += (_, _) => SetLine(hover: false);
    }

    private void SetLine(bool hover)
    {
        Brush? fill = null;
        var key = hover ? "SfAccentBrush" : "SfBorderBrush";
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true)
        {
            fill = value as Brush;
        }

        foreach (var rect in Children.OfType<Rectangle>())
        {
            if (fill is not null)
            {
                rect.Fill = fill;
            }

            rect.Width = hover ? 2 : 1;
        }
    }
}
