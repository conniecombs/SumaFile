using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SumaFile.Git.Model;

/// <summary>
/// The Git workbench's state, ported from 1.0.4's <c>GitWorkbenchViewModel</c>. The host draws the panel's chrome
/// (dock, detach and close), so the host-mode members are gone; the panel reads these values on
/// <see cref="PropertyChanged"/> (no bindings, spec §5.3). <see cref="Changes"/> is replaced whole by a new list, never
/// edited in place, so the panel can hand it to its list as <c>ItemsSource</c> without a copy.
/// </summary>
/// <remarks>The controller changes it on the UI thread; the panel marshals any other change to its dispatcher.</remarks>
public sealed class GitWorkbenchViewModel : INotifyPropertyChanged
{
    private const string DefaultDiffTitle = "Diff preview";
    private const string DefaultDiffText = "Select a changed file to preview its diff.";

    private string _branchText = "Git";
    private string _summaryText = "";
    private string _diffTitle = DefaultDiffTitle;
    private string _diffText = DefaultDiffText;
    private IReadOnlyList<GitChangeRow> _changes = new List<GitChangeRow>();
    private bool _isGitEnabled = true;
    private bool _isRepo;
    private bool _loading;
    private bool _commandRunning;
    private bool _isBusy;
    private int _selectedPathCount;
    private IReadOnlyList<GitChangeRow> _selectedChanges = [];
    private int _stagedCount;
    private bool _canRefresh = true;
    private bool _canFetch;
    private bool _canPull;
    private bool _canPush;
    private bool _canCommit;
    private bool _canStage;
    private bool _canUnstage;
    private bool _canDiscard;
    private bool _canDiff;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The changed paths of the last status, in the backend's order.</summary>
    public IReadOnlyList<GitChangeRow> Changes
    {
        get => _changes;
        private set => Set(ref _changes, value);
    }

    /// <summary>The rows selected in the panel's list, as the panel last reported them.</summary>
    public IReadOnlyList<GitChangeRow> SelectedChanges => _selectedChanges;

    /// <summary>True when <see cref="Changes"/> is empty: the panel shows "No changed files".</summary>
    public bool ChangesEmpty => _changes.Count == 0;

    /// <summary>True while a status refresh or a Git command runs; the action buttons are disabled.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    /// <summary>Whether the last status said the folder is inside a repository.</summary>
    public bool IsRepository => _isRepo;

    public string BranchText
    {
        get => _branchText;
        private set => Set(ref _branchText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    public string DiffTitle
    {
        get => _diffTitle;
        private set => Set(ref _diffTitle, value);
    }

    public string DiffText
    {
        get => _diffText;
        private set => Set(ref _diffText, value);
    }

    public bool CanRefresh
    {
        get => _canRefresh;
        private set => Set(ref _canRefresh, value);
    }

    public bool CanFetch
    {
        get => _canFetch;
        private set => Set(ref _canFetch, value);
    }

    public bool CanPull
    {
        get => _canPull;
        private set => Set(ref _canPull, value);
    }

    public bool CanPush
    {
        get => _canPush;
        private set => Set(ref _canPush, value);
    }

    public bool CanCommit
    {
        get => _canCommit;
        private set => Set(ref _canCommit, value);
    }

    public bool CanStage
    {
        get => _canStage;
        private set => Set(ref _canStage, value);
    }

    public bool CanUnstage
    {
        get => _canUnstage;
        private set => Set(ref _canUnstage, value);
    }

    public bool CanDiscard
    {
        get => _canDiscard;
        private set => Set(ref _canDiscard, value);
    }

    public bool CanDiff
    {
        get => _canDiff;
        private set => Set(ref _canDiff, value);
    }

    /// <summary>Disables the action buttons while a git command runs.</summary>
    public void SetCommandRunning(bool running)
    {
        _commandRunning = running;
        RefreshActions();
    }

    public void SetLoading()
    {
        _isGitEnabled = true;
        _loading = true;
        BranchText = "Git";
        SummaryText = "Checking Git status...";
        RefreshActions();
    }

    /// <summary>Ends a refresh that was cancelled before it had a result.</summary>
    public void SetLoadingCancelled()
    {
        if (!_loading)
        {
            return;
        }

        _loading = false;
        SummaryText = "Refresh was cancelled.";
        RefreshActions();
    }

    public void SetUnavailable(string message)
    {
        _isGitEnabled = true;
        _loading = false;
        _isRepo = false;
        _stagedCount = 0;
        BranchText = "Git";
        SummaryText = message;
        ReplaceChanges([]);
        ClearDiff();
        RefreshActions();
    }

    public void SetDisabled()
    {
        _isGitEnabled = false;
        _loading = false;
        _isRepo = false;
        _stagedCount = 0;
        BranchText = "Git";
        SummaryText = GitText.DisabledMessage;
        ReplaceChanges([]);
        ClearDiff();
        RefreshActions();
    }

    public void SetStatus(GitRepositoryStatus status, string summary)
    {
        ArgumentNullException.ThrowIfNull(status);
        _isGitEnabled = true;
        _loading = false;
        _isRepo = status.IsRepo;
        _stagedCount = status.Staged;
        BranchText = BranchLabel(status);
        SummaryText = summary;
        ReplaceChanges((status.Changes ?? []).Select(GitChangeRow.From));
        ClearDiff();
        RefreshActions();
    }

    /// <summary>
    /// The panel's selected rows and the paths an action would run on: the panel rows' paths, or, without a panel
    /// selection, the file list's selection filtered by the last status.
    /// </summary>
    public void UpdateSelection(IReadOnlyList<GitChangeRow> selectedChanges, IReadOnlyList<string> selectedPaths)
    {
        ArgumentNullException.ThrowIfNull(selectedChanges);
        ArgumentNullException.ThrowIfNull(selectedPaths);
        _selectedChanges = selectedChanges.ToArray();
        _selectedPathCount = selectedPaths.Count;
        RefreshActions();
    }

    public void SetDiffLoading(string title)
    {
        DiffTitle = string.IsNullOrWhiteSpace(title) ? DefaultDiffTitle : title;
        DiffText = "Loading diff...";
    }

    public void SetDiff(string title, string diff)
    {
        DiffTitle = string.IsNullOrWhiteSpace(title) ? DefaultDiffTitle : title;
        DiffText = string.IsNullOrWhiteSpace(diff) ? "No Git diff for the selected path." : diff;
    }

    public void ClearDiff()
    {
        DiffTitle = DefaultDiffTitle;
        DiffText = DefaultDiffText;
    }

    private void RefreshActions()
    {
        IsBusy = _loading || _commandRunning;
        var hasSelection = _selectedPathCount > 0;
        var hasPanelSelection = _selectedChanges.Count > 0;
        var ready = _isGitEnabled && !IsBusy;
        var repoReady = ready && _isRepo;

        CanRefresh = ready;
        CanFetch = repoReady;
        CanPull = repoReady;
        CanPush = repoReady;
        CanCommit = repoReady && _stagedCount > 0;
        CanStage = repoReady && hasSelection && (!hasPanelSelection || _selectedChanges.Any(row => row.CanStage));
        CanUnstage = repoReady && hasSelection && (!hasPanelSelection || _selectedChanges.Any(row => row.CanUnstage));
        CanDiscard = repoReady && hasSelection && (!hasPanelSelection || _selectedChanges.Any(row => row.CanDiscard));
        CanDiff = repoReady && _selectedPathCount == 1 && (!hasPanelSelection || _selectedChanges.Any(row => row.CanDiff));
    }

    private void ReplaceChanges(IEnumerable<GitChangeRow> rows)
    {
        var wasEmpty = ChangesEmpty;
        // A new List each time, the shape the remote plugin's lists bind as ItemsSource.
        Changes = new List<GitChangeRow>(rows);
        if (wasEmpty != ChangesEmpty)
        {
            OnPropertyChanged(nameof(ChangesEmpty));
        }

        _selectedChanges = [];
        _selectedPathCount = 0;
    }

    private static string BranchLabel(GitRepositoryStatus status)
    {
        if (!status.IsRepo)
        {
            return "Git";
        }

        var branch = !string.IsNullOrWhiteSpace(status.Branch)
            ? status.Branch
            : !string.IsNullOrWhiteSpace(status.Head)
                ? $"detached at {status.Head}"
                : "unknown branch";
        return string.IsNullOrWhiteSpace(status.Upstream)
            ? branch
            : $"{branch} -> {status.Upstream}";
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
