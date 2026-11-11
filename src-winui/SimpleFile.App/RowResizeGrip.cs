using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace SimpleFile.App;

/// <summary>
/// Horizontal splitter hit target, the north-south twin of <see cref="PaneResizeGrip"/>: subclassed so
/// <see cref="UIElement.ProtectedCursor"/> can show the north-south resize pointer. The docked bottom plugin panel uses it.
/// </summary>
public sealed class RowResizeGrip : Grid
{
    public RowResizeGrip()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        MinHeight = 8;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
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

            rect.Height = hover ? 2 : 1;
        }
    }
}
