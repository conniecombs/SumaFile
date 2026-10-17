using SimpleFile.Ipc;

namespace SimpleFile.Core;

internal sealed class WorkspaceTagController
{
    internal static readonly IReadOnlyList<Tag> DefaultTags =
    [
        new() { Name = "Red", Color = "#ef4444" },
        new() { Name = "Orange", Color = "#f97316" },
        new() { Name = "Yellow", Color = "#eab308" },
        new() { Name = "Green", Color = "#22c55e" },
        new() { Name = "Blue", Color = "#3b82f6" },
        new() { Name = "Purple", Color = "#a855f7" },
    ];

    public List<Tag> AllTags { get; private set; } = [];
    public Dictionary<string, Tag> FileTags { get; private set; } = new();
    public long? ActiveTagFilter { get; private set; }

    public async Task LoadTagsAsync(
        FileOperationService? fileOps,
        CancellationToken cancellationToken = default)
    {
        if (fileOps is null)
        {
            AllTags = [];
            FileTags = new();
            return;
        }

        try
        {
            var tags = await fileOps.GetAllTagsAsync(cancellationToken).ConfigureAwait(false);
            if (tags.Length == 0)
            {
                foreach (var dt in DefaultTags)
                {
                    await fileOps.CreateTagAsync(dt.Name, dt.Color, cancellationToken).ConfigureAwait(false);
                }

                tags = await fileOps.GetAllTagsAsync(cancellationToken).ConfigureAwait(false);
            }

            AllTags = [.. tags];
            FileTags = await fileOps.GetAllFileTagsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AllTags = [];
            FileTags = new();
        }
    }

    public async Task SetColorLabelAsync(
        FileOperationService fileOps,
        string[] paths,
        long tagId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileOps);
        foreach (var path in paths)
        {
            await fileOps.SetTagsForPathAsync(path, [tagId], cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        FileTags = await fileOps.GetAllFileTagsAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task RemoveColorLabelAsync(
        FileOperationService fileOps,
        string[] paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileOps);
        foreach (var path in paths)
        {
            await fileOps.SetTagsForPathAsync(path, [], cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        FileTags = await fileOps.GetAllFileTagsAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task UpdateTagAsync(
        FileOperationService fileOps,
        long id,
        string name,
        string color,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileOps);
        await fileOps.UpdateTagAsync(id, name, color, cancellationToken).ConfigureAwait(false);
        await LoadTagsAsync(fileOps, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteTagDefinitionAsync(
        FileOperationService fileOps,
        long id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileOps);
        await fileOps.DeleteTagAsync(id, cancellationToken).ConfigureAwait(false);
        await LoadTagsAsync(fileOps, cancellationToken).ConfigureAwait(false);
    }

    public void SetTagFilter(long? tagId)
    {
        ActiveTagFilter = tagId;
    }

    public IReadOnlyList<string> FilesWithTag(long tagId)
    {
        return FileTags.Where(pair => pair.Value.Id == tagId).Select(pair => pair.Key).ToList();
    }

    public IReadOnlyList<FileEntry> FilterEntries(IReadOnlyList<FileEntry> entries)
    {
        if (ActiveTagFilter is not long tagId)
        {
            return entries;
        }

        var tagged = FileTags
            .Where(pair => pair.Value.Id == tagId)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return entries.Where(entry => tagged.Contains(entry.Path)).ToList();
    }

    public async Task SetColorLabelAsync(
        ExplorerWorkspace workspace,
        string[] paths,
        long tagId,
        CancellationToken cancellationToken = default)
    {
        await SetColorLabelAsync(workspace.RequireFileOps(), paths, tagId, cancellationToken).ConfigureAwait(false);
        workspace.NotifyChanged();
    }

    public async Task RemoveColorLabelAsync(
        ExplorerWorkspace workspace,
        string[] paths,
        CancellationToken cancellationToken = default)
    {
        await RemoveColorLabelAsync(workspace.RequireFileOps(), paths, cancellationToken).ConfigureAwait(false);
        workspace.NotifyChanged();
    }

    public void SetTagFilter(ExplorerWorkspace workspace, long? tagId)
    {
        SetTagFilter(tagId);
        workspace.NotifyChanged();
    }

    public async Task UpdateTagAsync(
        ExplorerWorkspace workspace,
        long id,
        string name,
        string color,
        CancellationToken cancellationToken = default)
    {
        await UpdateTagAsync(workspace.RequireFileOps(), id, name, color, cancellationToken).ConfigureAwait(false);
        workspace.NotifyChanged();
    }

    public async Task DeleteTagDefinitionAsync(
        ExplorerWorkspace workspace,
        long id,
        CancellationToken cancellationToken = default)
    {
        await DeleteTagDefinitionAsync(workspace.RequireFileOps(), id, cancellationToken).ConfigureAwait(false);
        workspace.NotifyChanged();
    }
}
