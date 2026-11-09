using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;

namespace SumaFile.Remote.Views;

/// <summary>
/// Virtualized rows without bindings. A list binds model objects through <c>ItemsSource</c> and one row template
/// parsed once; WinUI realizes (and recycles) containers only for the rows in view, and <see cref="Attach{T}"/> fills
/// each realized row's named elements from its model in <c>ContainerContentChanging</c>. Plugin windows share the
/// host's UI thread, so a listing of tens of thousands of entries must not build one element tree per entry.
/// </summary>
internal static class ListRows
{
    /// <summary>
    /// Gives <paramref name="list"/> its row template and fills each realized row with
    /// <paramref name="fill"/>(container, template root, model). Returns the call that unhooks it.
    /// </summary>
    public static Action Attach<T>(ListViewBase list, DataTemplate template, Action<SelectorItem, FrameworkElement, T> fill)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(fill);

        list.ItemTemplate = template;
        TypedEventHandler<ListViewBase, ContainerContentChangingEventArgs>? handler = null;
        handler = (_, args) =>
        {
            if (args.InRecycleQueue)
            {
                return;
            }

            if (args.ItemContainer is { ContentTemplateRoot: FrameworkElement root } container && args.Item is T model)
            {
                container.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                fill(container, root, model);
            }
            else if (args.Phase < 2)
            {
                // The template is not applied yet: try again in the next phase.
                args.RegisterUpdateCallback(handler);
            }

            args.Handled = true;
        };

        list.ContainerContentChanging += handler;
        return () => list.ContainerContentChanging -= handler;
    }

    /// <summary>
    /// The model an <c>ItemClick</c> reported. For an <c>ItemsSource</c> list WinUI passes the model itself; a
    /// container, or a container's content, is mapped back to its model too, so a handler never depends on which.
    /// </summary>
    public static T? Clicked<T>(ListViewBase list, object? clickedItem) where T : class
    {
        ArgumentNullException.ThrowIfNull(list);
        switch (clickedItem)
        {
            case null:
                return null;
            case T model:
                return model;
            case SelectorItem container:
                return list.ItemFromContainer(container) as T;
        }

        if (list.ItemsPanelRoot is { } panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is SelectorItem container
                    && (ReferenceEquals(container.Content, clickedItem) || ReferenceEquals(container.ContentTemplateRoot, clickedItem)))
                {
                    return list.ItemFromContainer(container) as T;
                }
            }
        }

        return null;
    }

    /// <summary>The template root of <paramref name="model"/>'s row while it is realized, otherwise null.</summary>
    public static FrameworkElement? RealizedRow(ListViewBase list, object model) =>
        list.ContainerFromItem(model) is SelectorItem { ContentTemplateRoot: FrameworkElement root } ? root : null;
}
