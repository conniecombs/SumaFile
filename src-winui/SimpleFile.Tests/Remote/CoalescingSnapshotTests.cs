using System.Collections.ObjectModel;
using SumaFile.Remote.Model;
using Xunit;

namespace SimpleFile.Tests.Remote;

/// <summary>
/// The manager's lists show a virtualized snapshot of the view model's collections. The view model fills a folder
/// listing one <c>Add</c> at a time, so the snapshot coalesces every change made in one UI-thread turn into a single
/// publish, which keeps a large folder (tens of thousands of entries) from costing one list update per entry.
/// </summary>
public class CoalescingSnapshotTests
{
    [Fact]
    public void Constructor_PublishesTheCurrentItemsAtOnce()
    {
        var source = new ObservableCollection<string>(["a", "b"]);
        var queue = new Queue<Action>();
        var published = new List<IReadOnlyList<string>>();

        using var snapshot = new CoalescingSnapshot<string>(source, Enqueue(queue), published.Add);

        Assert.Equal(["a", "b"], Assert.Single(published));
        Assert.Empty(queue);
    }

    [Fact]
    public void ManyChangesInOneTurn_PublishOnceWithTheFinalItems()
    {
        var source = new ObservableCollection<int>();
        var queue = new Queue<Action>();
        var published = new List<IReadOnlyList<int>>();
        using var snapshot = new CoalescingSnapshot<int>(source, Enqueue(queue), published.Add);
        published.Clear();

        source.Add(99);
        source.Clear();
        for (var i = 0; i < 20_000; i++)
        {
            source.Add(i);
        }

        source.RemoveAt(0);
        source.Insert(0, -1);

        Assert.Empty(published);
        Assert.Single(queue);
        queue.Dequeue()();

        var items = Assert.Single(published);
        Assert.Equal(20_000, items.Count);
        Assert.Equal(-1, items[0]);
        Assert.Equal(19_999, items[^1]);
    }

    [Fact]
    public void ChangesAfterAPublish_ScheduleTheNextOne()
    {
        var source = new ObservableCollection<string>();
        var queue = new Queue<Action>();
        var published = new List<IReadOnlyList<string>>();
        using var snapshot = new CoalescingSnapshot<string>(source, Enqueue(queue), published.Add);

        source.Add("one");
        queue.Dequeue()();
        source.Add("two");
        Assert.Single(queue);
        queue.Dequeue()();

        Assert.Equal(["one", "two"], published[^1]);
        Assert.Equal(3, published.Count);
    }

    [Fact]
    public void Snapshot_IsACopyTheSourceCannotChange()
    {
        var source = new ObservableCollection<string>(["a"]);
        var queue = new Queue<Action>();
        var published = new List<IReadOnlyList<string>>();
        using var snapshot = new CoalescingSnapshot<string>(source, Enqueue(queue), published.Add);

        source.Add("b");

        Assert.Equal(["a"], published[0]);
    }

    [Fact]
    public void Dispose_StopsPublishing_EvenWithAPublishPending()
    {
        var source = new ObservableCollection<string>();
        var queue = new Queue<Action>();
        var published = new List<IReadOnlyList<string>>();
        var snapshot = new CoalescingSnapshot<string>(source, Enqueue(queue), published.Add);
        published.Clear();

        source.Add("pending");
        snapshot.Dispose();
        queue.Dequeue()();
        source.Add("after");

        Assert.Empty(published);
        Assert.Empty(queue);
    }

    [Fact]
    public void AFailedEnqueue_IsRetriedOnTheNextChange()
    {
        // TryEnqueue fails while the dispatcher shuts down; a later change must still be able to schedule.
        var source = new ObservableCollection<string>();
        var queue = new Queue<Action>();
        var accept = false;
        var published = new List<IReadOnlyList<string>>();
        using var snapshot = new CoalescingSnapshot<string>(
            source,
            action =>
            {
                if (accept)
                {
                    queue.Enqueue(action);
                }

                return accept;
            },
            published.Add);

        source.Add("dropped");
        accept = true;
        source.Add("kept");

        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Equal(["dropped", "kept"], published[^1]);
    }

    private static Func<Action, bool> Enqueue(Queue<Action> queue) => action =>
    {
        queue.Enqueue(action);
        return true;
    };
}
