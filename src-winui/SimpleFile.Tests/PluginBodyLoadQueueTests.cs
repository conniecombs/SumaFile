using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class PluginBodyLoadQueueTests
{
    /// <summary>Captures what the queue hands its dispatcher, so a test decides when each callback runs.</summary>
    private sealed class CapturedDispatcher
    {
        private readonly List<Action> _queued = [];

        public int Count => _queued.Count;

        public void Enqueue(Action callback) => _queued.Add(callback);

        public void RunAll()
        {
            var queued = _queued.ToArray();
            _queued.Clear();
            foreach (var callback in queued) callback();
        }
    }

    [Fact]
    public void Request_NeverRunsTheLoadInline()
    {
        var dispatcher = new CapturedDispatcher();
        var queue = new PluginBodyLoadQueue(dispatcher.Enqueue);
        var loads = 0;

        Assert.True(queue.Request("sidebar:a", () =>
        {
            loads++;
            return Task.CompletedTask;
        }));

        Assert.Equal(0, loads);
        Assert.True(queue.IsPending("sidebar:a"));
        Assert.Equal(1, dispatcher.Count);

        dispatcher.RunAll();

        Assert.Equal(1, loads);
        Assert.False(queue.IsPending("sidebar:a"));
    }

    [Fact]
    public void Request_SameKeyWhilePending_IsQueuedOnce()
    {
        var dispatcher = new CapturedDispatcher();
        var queue = new PluginBodyLoadQueue(dispatcher.Enqueue);
        var loads = 0;
        Task Load()
        {
            loads++;
            return Task.CompletedTask;
        }

        Assert.True(queue.Request("sidebar:a", Load));
        Assert.False(queue.Request("sidebar:a", Load));
        // Another key, and the same id in another slot, queue on their own.
        Assert.True(queue.Request("sidebar:b", Load));
        Assert.True(queue.Request("panel:a", Load));

        Assert.Equal(3, dispatcher.Count);
        dispatcher.RunAll();
        Assert.Equal(3, loads);
    }

    [Fact]
    public void Forget_DropsAQueuedLoad()
    {
        var dispatcher = new CapturedDispatcher();
        var queue = new PluginBodyLoadQueue(dispatcher.Enqueue);
        var loads = new List<string>();

        queue.Request("sidebar:a", () =>
        {
            loads.Add("first");
            return Task.CompletedTask;
        });
        queue.Forget("sidebar:a");
        Assert.False(queue.IsPending("sidebar:a"));

        // A new request after the forget runs; the forgotten one never does.
        queue.Request("sidebar:a", () =>
        {
            loads.Add("second");
            return Task.CompletedTask;
        });
        dispatcher.RunAll();

        Assert.Equal(["second"], loads);
    }

    [Fact]
    public void Request_AfterTheLoadStarted_QueuesAgain()
    {
        var dispatcher = new CapturedDispatcher();
        var queue = new PluginBodyLoadQueue(dispatcher.Enqueue);
        var running = new TaskCompletionSource();
        var starts = 0;
        Task Load()
        {
            starts++;
            return running.Task;
        }

        queue.Request("sidebar:a", Load);
        dispatcher.RunAll();
        Assert.Equal(1, starts);

        // The pending mark was cleared before the load started, so a request while it runs queues again.
        Assert.False(queue.IsPending("sidebar:a"));
        Assert.True(queue.Request("sidebar:a", Load));
        dispatcher.RunAll();
        Assert.Equal(2, starts);
        running.SetResult();
    }
}
