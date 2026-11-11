using SimpleFile.PluginSdk;

namespace SimpleFile.Tests.Git;

/// <summary>
/// An <see cref="IPluginHost"/> for <c>com.sumafile.git</c> that records what the Git UI asks of the host:
/// <see cref="IPluginDialogs.Notify"/>, <see cref="IPluginDecorations.Invalidate"/>,
/// <see cref="IPluginWorkspace.RefreshAsync"/>, <see cref="IPluginPanels.ShowAsync"/> and
/// <see cref="IPluginPanels.Hide"/>. <see cref="IPluginPanels.IsVisible"/> answers from <see cref="PanelVisible"/>, and
/// the settings come from <see cref="SettingValues"/>. The Git UI reaches its backend through <c>IGitBackend</c>, so
/// a backend call here fails the test.
/// </summary>
internal sealed class FakeGitHost : IPluginHost
{
    private readonly object _gate = new();
    private readonly List<(string Message, bool IsError)> _notifications = [];
    private readonly List<string> _panelCalls = [];
    private int _invalidations;
    private int _refreshes;

    public FakeGitHost()
    {
        Settings = new FakeSettings(this);
        Dialogs = new FakeDialogs(this);
        Workspace = new FakeWorkspace(this);
        Panels = new FakePanels(this);
        Decorations = new FakeDecorations(this);
    }

    public string PluginId => "com.sumafile.git";

    /// <summary>The plugin's settings by key; a missing key reads as null.</summary>
    public Dictionary<string, string> SettingValues { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, <see cref="IPluginSettings.GetAsync"/> fails with it (a settings store that does not answer).</summary>
    public Exception? SettingsFailure { get; set; }

    /// <summary>What <see cref="IPluginPanels.IsVisible"/> answers for every panel id.</summary>
    public bool PanelVisible { get; set; }

    /// <summary>When set, <see cref="IPluginPanels.ShowAsync"/> also makes <see cref="PanelVisible"/> true.</summary>
    public bool ShowMakesVisible { get; set; } = true;

    public string? ActivePath { get; set; }

    public IReadOnlyList<string> SelectedPaths { get; set; } = [];

    public FakeLogger Log { get; } = new();

    public IPluginSettings Settings { get; }
    public IPluginProgress Progress => throw new NotSupportedException();
    public IPluginDialogs Dialogs { get; }
    public IPluginWorkspace Workspace { get; }
    public IPluginLogger Logger => Log;
    public IPluginWindows Windows => throw new NotSupportedException();
    public IPluginPanels Panels { get; }
    public IPluginDecorations Decorations { get; }

    public IReadOnlyList<(string Message, bool IsError)> Notifications
    {
        get
        {
            lock (_gate)
            {
                return _notifications.ToArray();
            }
        }
    }

    /// <summary><c>show:&lt;id&gt;</c> and <c>hide:&lt;id&gt;</c>, in call order.</summary>
    public IReadOnlyList<string> PanelCalls
    {
        get
        {
            lock (_gate)
            {
                return _panelCalls.ToArray();
            }
        }
    }

    public int Invalidations => Volatile.Read(ref _invalidations);

    public int WorkspaceRefreshes => Volatile.Read(ref _refreshes);

    public Task<T> InvokeBackendAsync<T>(string method, object? args, CancellationToken ct) =>
        throw new NotSupportedException($"The Git UI called the host backend directly ({method}).");

    public IDisposable OnBackendEvent<T>(string eventName, Action<T> handler) => throw new NotSupportedException();

    public void RaiseWorkspaceChanged() => ((FakeWorkspace)Workspace).Raise();

    private void Record(List<string> list, string entry)
    {
        lock (_gate)
        {
            list.Add(entry);
        }
    }

    internal sealed class FakeLogger : IPluginLogger
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToArray();
                }
            }
        }

        public void Info(string message) => Add("info " + message);
        public void Warn(string message) => Add("warn " + message);
        public void Error(string message, Exception? exception = null) => Add("error " + message);

        private void Add(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }
    }

    private sealed class FakeSettings(FakeGitHost owner) : IPluginSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (owner.SettingsFailure is { } failure)
            {
                return Task.FromException<string?>(failure);
            }

            lock (owner._gate)
            {
                return Task.FromResult(owner.SettingValues.TryGetValue(key, out var value) ? value : null);
            }
        }

        public Task SetAsync(string key, string value, CancellationToken ct)
        {
            lock (owner._gate)
            {
                owner.SettingValues[key] = value;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeDialogs(FakeGitHost owner) : IPluginDialogs
    {
        public Task ShowMessageAsync(string title, string message, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> PickFolderAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<PluginDialogResult> ShowDialogAsync(object contentDialog, CancellationToken ct) => throw new NotSupportedException();

        public void Notify(string message, bool isError)
        {
            lock (owner._gate)
            {
                owner._notifications.Add((message, isError));
            }
        }
    }

    private sealed class FakeWorkspace(FakeGitHost owner) : IPluginWorkspace
    {
        public string? ActivePath => owner.ActivePath;
        public IReadOnlyList<string> SelectedPaths => owner.SelectedPaths;
        public event EventHandler? Changed;

        public Task NavigateAsync(string path, CancellationToken ct) => throw new NotSupportedException();

        public Task RefreshAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref owner._refreshes);
            return Task.CompletedTask;
        }

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakePanels(FakeGitHost owner) : IPluginPanels
    {
        public Task ShowAsync(string panelId, CancellationToken ct)
        {
            owner.Record(owner._panelCalls, "show:" + panelId);
            if (owner.ShowMakesVisible)
            {
                owner.PanelVisible = true;
            }

            return Task.CompletedTask;
        }

        public void Hide(string panelId) => owner.Record(owner._panelCalls, "hide:" + panelId);

        public bool IsVisible(string panelId) => owner.PanelVisible;
    }

    private sealed class FakeDecorations(FakeGitHost owner) : IPluginDecorations
    {
        public void Invalidate() => Interlocked.Increment(ref owner._invalidations);
    }
}
