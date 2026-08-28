using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>
/// Per-pane listing, history, and tabs. Mirrors primary vs secondary fields
/// in frontend/src/lib/app/core.ts.
/// </summary>
public sealed class ExplorerPane
{
    public ExplorerPane(PaneId id)
    {
        Id = id;
    }

    public PaneId Id { get; }

    public string Path { get; set; } = "";
    public List<FileEntry> Entries { get; set; } = [];
    public List<string> History { get; } = [];
    public int HistoryIndex { get; set; } = -1;
    public List<FileTab> Tabs { get; } = [];
    public string? ActiveTabId { get; set; }
    public string? SelectedPath { get; set; }
    public bool IsNavigating { get; set; }
    public bool ListingInProgress { get; set; }
    public bool PathIsNetwork { get; set; }
    public string View { get; set; } = "details";
    public int IconSize { get; set; } = 16;
    public string SortBy { get; set; } = "name";
    public bool SortAscending { get; set; } = true;
    private int _navigationToken;

    public int NextNavigationToken() => Interlocked.Increment(ref _navigationToken);

    public int NavigationToken => Volatile.Read(ref _navigationToken);

    public bool CanGoBack => HistoryIndex > 0;
    public bool CanGoForward => HistoryIndex >= 0 && HistoryIndex < History.Count - 1;
    public bool CanGoUp => PathRules.GetParentPath(Path) is not null;

    public IReadOnlyList<BreadcrumbSegment> Breadcrumbs => BreadcrumbBuilder.FromPath(Path);

    public IReadOnlyList<FileEntry> VisibleEntries(
        string sortBy,
        bool sortAscending,
        bool showHidden,
        string filterQuery,
        bool keepFoldersOnTop = true)
    {
        return EntryPresentation.VisibleEntries(Entries, filterQuery, showHidden, sortBy, sortAscending, keepFoldersOnTop);
    }

    public IReadOnlyList<FileEntry> VisibleEntries(bool showHidden, string filterQuery, bool keepFoldersOnTop = true)
    {
        return VisibleEntries(SortBy, SortAscending, showHidden, filterQuery, keepFoldersOnTop);
    }

    public void ApplyViewDefaults(UiSettings settings)
    {
        View = UiSettings.NormalizeDefaultView(settings.DefaultView);
        IconSize = UiSettings.NormalizeIconSize(settings.DefaultIconSize);
        SortBy = "name";
        SortAscending = true;
    }

    public void SetView(string view)
    {
        View = UiSettings.NormalizeDefaultView(view);
    }

    public void SetIconSize(int iconSize)
    {
        IconSize = UiSettings.NormalizeIconSize(iconSize);
    }

    public void SetSort(string sortBy)
    {
        if (string.Equals(SortBy, sortBy, StringComparison.OrdinalIgnoreCase))
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortBy = sortBy;
            SortAscending = true;
        }
    }

    public void CopyViewOptionsFrom(ExplorerPane source)
    {
        View = source.View;
        IconSize = source.IconSize;
        SortBy = source.SortBy;
        SortAscending = source.SortAscending;
    }

    public void RecordHistory(string path, HistoryMode mode)
    {
        if (mode == HistoryMode.None)
        {
            return;
        }

        if (mode == HistoryMode.ReplaceCurrent && HistoryIndex >= 0)
        {
            History[HistoryIndex] = path;
            return;
        }

        if (HistoryIndex >= 0 && HistoryIndex < History.Count && History[HistoryIndex] == path)
        {
            return;
        }

        if (HistoryIndex + 1 < History.Count)
        {
            History.RemoveRange(HistoryIndex + 1, History.Count - HistoryIndex - 1);
        }

        History.Add(path);
        HistoryIndex = History.Count - 1;
    }

    public void SyncActiveTab()
    {
        if (string.IsNullOrEmpty(Path))
        {
            return;
        }

        var tabId = ActiveTabId ?? $"tab-{Id.ToString().ToLowerInvariant()}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var tab = new FileTab
        {
            Id = tabId,
            Path = Path,
            Title = PathRules.Basename(Path),
            History = [.. History],
            HistoryIndex = HistoryIndex,
        };

        var index = Tabs.FindIndex(candidate => candidate.Id == tabId);
        if (index >= 0)
        {
            Tabs[index] = tab;
        }
        else
        {
            Tabs.Add(tab);
        }

        ActiveTabId = tabId;
    }

    public static FileTab CreateTab(string path)
    {
        return new FileTab
        {
            Id = $"tab-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Random.Shared.Next():x}",
            Path = path,
            Title = PathRules.Basename(path),
            History = [path],
            HistoryIndex = 0,
        };
    }

    public void EnsureActiveTab(string? fallbackPath = null)
    {
        if (ActiveTabId is not null && Tabs.Any(tab => tab.Id == ActiveTabId))
        {
            return;
        }

        if (Tabs.Count > 0)
        {
            ActiveTabId = Tabs[0].Id;
            return;
        }

        var path = string.IsNullOrEmpty(Path) ? fallbackPath : Path;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var tab = CreateTab(path);
        if (History.Count > 0)
        {
            tab.History = [.. History];
            tab.HistoryIndex = HistoryIndex >= 0 && HistoryIndex < tab.History.Count
                ? HistoryIndex
                : tab.History.Count - 1;
        }

        Tabs.Add(tab);
        ActiveTabId = tab.Id;
    }

    public void ApplyTabHistory(FileTab tab)
    {
        History.Clear();
        History.AddRange(tab.History.Count > 0 ? tab.History : [tab.Path]);
        HistoryIndex = tab.HistoryIndex >= 0 && tab.HistoryIndex < History.Count
            ? tab.HistoryIndex
            : History.Count - 1;
        ActiveTabId = tab.Id;
    }

    public void SwapContents(ExplorerPane other)
    {
        (Path, other.Path) = (other.Path, Path);
        (Entries, other.Entries) = (other.Entries, Entries);
        var history = History.ToList();
        var historyIndex = HistoryIndex;
        History.Clear();
        History.AddRange(other.History);
        HistoryIndex = other.HistoryIndex;
        other.History.Clear();
        other.History.AddRange(history);
        other.HistoryIndex = historyIndex;

        var tabs = Tabs.Select(tab => tab.Clone()).ToList();
        var activeTabId = ActiveTabId;
        Tabs.Clear();
        Tabs.AddRange(other.Tabs.Select(tab => tab.Clone()));
        ActiveTabId = other.ActiveTabId;
        other.Tabs.Clear();
        other.Tabs.AddRange(tabs);
        other.ActiveTabId = activeTabId;

        (View, other.View) = (other.View, View);
        (IconSize, other.IconSize) = (other.IconSize, IconSize);
        (SortBy, other.SortBy) = (other.SortBy, SortBy);
        (SortAscending, other.SortAscending) = (other.SortAscending, SortAscending);

        (SelectedPath, other.SelectedPath) = (other.SelectedPath, SelectedPath);
        (PathIsNetwork, other.PathIsNetwork) = (other.PathIsNetwork, PathIsNetwork);
        IsNavigating = false;
        other.IsNavigating = false;
        ListingInProgress = false;
        other.ListingInProgress = false;
    }
}
