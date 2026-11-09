using System.Text.Json;
using SimpleFile.Ipc;
using SimpleFile.PluginSdk;

namespace SimpleFile.Tests.Remote;

/// <summary>
/// An <see cref="IPluginHost"/> that records every backend call (method and args JSON, serialized with the
/// host's own <see cref="IpcJson.Options"/>), answers from per-method responders and raises backend events.
/// </summary>
internal sealed class FakePluginHost : IPluginHost
{
    private readonly object _gate = new();
    private readonly List<BackendCall> _calls = [];
    private readonly Dictionary<string, List<Subscription>> _subscriptions = new(StringComparer.Ordinal);

    public string PluginId => "com.sumafile.remote";

    /// <summary>Per method: args JSON in, result JSON out. A method without one answers <c>null</c>.</summary>
    public Dictionary<string, Func<string, Task<string>>> Responders { get; } = new(StringComparer.Ordinal);

    public RecordingLogger Log { get; } = new();

    public IPluginSettings Settings => throw new NotSupportedException();
    public IPluginProgress Progress => throw new NotSupportedException();
    public IPluginDialogs Dialogs => throw new NotSupportedException();
    public IPluginWorkspace Workspace => throw new NotSupportedException();
    public IPluginLogger Logger => Log;
    public IPluginWindows Windows => throw new NotSupportedException();
    public IPluginPanels Panels => throw new NotSupportedException();
    public IPluginDecorations Decorations => throw new NotSupportedException();

    public IReadOnlyList<BackendCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    public void Respond(string method, string resultJson) =>
        Responders[method] = _ => Task.FromResult(resultJson);

    public async Task<T> InvokeBackendAsync<T>(string method, object? args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var json = args is null ? "null" : JsonSerializer.Serialize(args, args.GetType(), IpcJson.Options);
        lock (_gate)
        {
            _calls.Add(new BackendCall(method, json));
        }

        var result = Responders.TryGetValue(method, out var responder)
            ? await responder(json).WaitAsync(ct).ConfigureAwait(false)
            : "null";
        if (result == "null")
        {
            return default(T) is null ? default! : throw new InvalidOperationException($"{method} returned no result.");
        }

        return JsonSerializer.Deserialize<T>(result, IpcJson.Options)
            ?? throw new InvalidOperationException($"{method} returned no result.");
    }

    public IDisposable OnBackendEvent<T>(string eventName, Action<T> handler)
    {
        var subscription = new Subscription(this, eventName, json =>
        {
            var value = JsonSerializer.Deserialize<T>(json, IpcJson.Options);
            if (value is not null)
            {
                handler(value);
            }
        });
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(eventName, out var list))
            {
                _subscriptions[eventName] = list = [];
            }

            list.Add(subscription);
        }

        return subscription;
    }

    /// <summary>Delivers <paramref name="json"/> to every live subscription of <paramref name="eventName"/>.</summary>
    public void Raise(string eventName, string json)
    {
        Subscription[] targets;
        lock (_gate)
        {
            targets = _subscriptions.TryGetValue(eventName, out var list) ? list.ToArray() : [];
        }

        foreach (var target in targets)
        {
            target.Deliver(json);
        }
    }

    public int SubscriptionCount(string eventName)
    {
        lock (_gate)
        {
            return _subscriptions.TryGetValue(eventName, out var list) ? list.Count : 0;
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(subscription.EventName, out var list))
            {
                list.Remove(subscription);
            }
        }
    }

    internal sealed record BackendCall(string Method, string ArgsJson);

    internal sealed class RecordingLogger : IPluginLogger
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) => Lines.Add("info " + message);
        public void Warn(string message) => Lines.Add("warn " + message);
        public void Error(string message, Exception? exception = null) => Lines.Add("error " + message);
    }

    private sealed class Subscription(FakePluginHost owner, string eventName, Action<string> deliver) : IDisposable
    {
        private int _disposed;

        public string EventName { get; } = eventName;

        public void Deliver(string json)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                deliver(json);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Remove(this);
            }
        }
    }
}
