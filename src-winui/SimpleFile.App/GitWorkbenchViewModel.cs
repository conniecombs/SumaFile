using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using SimpleFile.Ipc;

namespace SimpleFile.App;

public sealed class GitWorkbenchViewModel : INotifyPropertyChanged
{
    private string _branchText = "Git";
    private string _summaryText = "";
    private string _diffTitle = "Diff preview";
    private string _diffText = "Select a changed file to preview its diff.";
    private string _hostActionText = "Detach";
    private string _hostActionToolTip = "Detach Git workbench";
    private bool _isDetached;
    private bool _isGitEnabled = true;
    private bool _isRepo;
    private bool _isBusy;
    private int _selectedPathCount;
    private GitChangeRow[] _selectedChanges = [];
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

    public ObservableCollection<GitChangeRow> Changes { get; } = [];

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

    public string HostActionText
    {
        get => _hostActionText;
        private set => Set(ref _hostActionText, value);
    }

    public string HostActionToolTip
    {
        get => _hostActionToolTip;
        private set => Set(ref _hostActionToolTip, value);
    }

    public bool IsDetached
    {
        get => _isDetached;
        private set => Set(ref _isDetached, value);
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

    public Visibility ChangesEmptyVisibility => Changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public void SetHostMode(bool detached)
    {
        IsDetached = detached;
        HostActionText = detached ? "Dock" : "Detach";
        HostActionToolTip = detached ? "Dock Git workbench" : "Detach Git workbench";
    }

    public void SetLoading()
    {
        _isGitEnabled = true;
        _isBusy = true;
        BranchText = "Git";
        SummaryText = "Checking Git status...";
        RefreshActions();
    }

    public void SetUnavailable(string message)
    {
        _isGitEnabled = true;
        _isBusy = false;
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
        _isBusy = false;
        _isRepo = false;
        _stagedCount = 0;
        BranchText = "Git";
        SummaryText = "Git integration is disabled.";
        ReplaceChanges([]);
        ClearDiff();
        RefreshActions();
    }

    public void SetStatus(GitRepositoryStatus status, string summary)
    {
        _isGitEnabled = true;
        _isBusy = false;
        _isRepo = status.IsRepo;
        _stagedCount = status.Staged;
        BranchText = BranchLabel(status);
        SummaryText = summary;
        ReplaceChanges(status.Changes.Select(GitChangeRow.From));
        ClearDiff();
        RefreshActions();
    }

    public void UpdateSelection(GitChangeRow[] selectedChanges, string[] selectedPaths)
    {
        _selectedChanges = selectedChanges;
        _selectedPathCount = selectedPaths.Length;
        RefreshActions();
    }

    public void SetDiffLoading(string title)
    {
        DiffTitle = string.IsNullOrWhiteSpace(title) ? "Diff preview" : title;
        DiffText = "Loading diff...";
    }

    public void SetDiff(string title, string diff)
    {
        DiffTitle = string.IsNullOrWhiteSpace(title) ? "Diff preview" : title;
        DiffText = string.IsNullOrWhiteSpace(diff) ? "No Git diff for the selected path." : diff;
    }

    public void ClearDiff()
    {
        DiffTitle = "Diff preview";
        DiffText = "Select a changed file to preview its diff.";
    }

    private void RefreshActions()
    {
        var hasSelection = _selectedPathCount > 0;
        var hasPanelSelection = _selectedChanges.Length > 0;
        var ready = _isGitEnabled && !_isBusy;
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
        Changes.Clear();
        foreach (var row in rows)
        {
            Changes.Add(row);
        }

        OnPropertyChanged(nameof(ChangesEmptyVisibility));
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

    private void OnPropertyChanged(string? propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
