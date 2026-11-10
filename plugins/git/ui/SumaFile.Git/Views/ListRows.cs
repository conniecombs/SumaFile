using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;

namespace SumaFile.Git.Views;

/// <summary>
/// Virtualized rows without bindings, as in the remote plugin. A list binds model objects through <c>ItemsSource</c>
/// and one row template parsed once; WinUI realizes (and recycles) containers only for the rows in view, and
/// <see cref="Attach{T}"/> fills each realized row's named elements from its model in <c>ContainerContentChanging</c>.
/// A repository with thousands of changes must not build one element tree per change on the host's UI thread.
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
}
