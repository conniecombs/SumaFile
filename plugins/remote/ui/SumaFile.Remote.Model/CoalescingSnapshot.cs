using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace SumaFile.Remote.Model;

/// <summary>
/// Publishes copies of an <see cref="ObservableCollection{T}"/>, at most one per UI-thread turn. The view model fills
/// a folder listing one <c>Add</c> at a time; a list bound to the collection itself would take one update per entry,
/// and a large folder would stall the host's UI thread, which plugin windows share. The window binds each published
/// copy as its list's <c>ItemsSource</c> instead. All calls happen on the UI thread; WinUI-free so it can be tested.
/// </summary>
public sealed class CoalescingSnapshot<T> : IDisposable
{
    private readonly ObservableCollection<T> _source;
    private readonly Func<Action, bool> _enqueue;
    private readonly Action<IReadOnlyList<T>> _publish;
    private bool _pending;
    private bool _disposed;

    /// <param name="source">The collection to follow.</param>
    /// <param name="enqueue">Runs an action later on the UI thread (<c>DispatcherQueue.TryEnqueue</c>); false when it cannot.</param>
    /// <param name="publish">Receives each copy, a new <see cref="List{T}"/> (WinUI binds it as a vector); the first is published from the constructor.</param>
    public CoalescingSnapshot(ObservableCollection<T> source, Func<Action, bool> enqueue, Action<IReadOnlyList<T>> publish)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _source.CollectionChanged += OnCollectionChanged;
        _publish(new List<T>(_source));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.CollectionChanged -= OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed || _pending)
        {
            return;
        }

        // A failed enqueue (the dispatcher is shutting down) leaves nothing pending, so a later change retries.
        _pending = true;
        if (!_enqueue(Flush))
        {
            _pending = false;
        }
    }

    private void Flush()
    {
        _pending = false;
        if (!_disposed)
        {
            _publish(new List<T>(_source));
        }
    }
}
