using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed partial class ExplorerWorkspace
{

    public Task<WorkspaceProfile> SaveWorkspaceProfileAsync(
        string name,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
        => _profiles.SaveAsync(name, overwrite, cancellationToken);

    public Task<WorkspaceProfile> OverwriteWorkspaceProfileAsync(
        string id,
        CancellationToken cancellationToken = default)
        => _profiles.OverwriteAsync(id, cancellationToken);

    public Task<WorkspaceProfile> DuplicateWorkspaceProfileAsync(
        string id,
        string? name = null,
        CancellationToken cancellationToken = default)
        => _profiles.DuplicateAsync(id, name, cancellationToken);

    public Task<WorkspaceProfile> RenameWorkspaceProfileAsync(
        string id,
        string name,
        CancellationToken cancellationToken = default)
        => _profiles.RenameAsync(id, name, cancellationToken);

    public Task<WorkspaceProfile> ResetWorkspaceProfileAsync(
        string id,
        CancellationToken cancellationToken = default)
        => _profiles.ResetAsync(id, cancellationToken);

    public Task DeleteWorkspaceProfileAsync(string id, CancellationToken cancellationToken = default)
        => _profiles.DeleteAsync(id, cancellationToken);

    public async Task ApplyWorkspaceProfileAsync(string id, CancellationToken cancellationToken = default)
    {
        var profile = await _profiles.FindAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Profile was not found.");
        var clone = profile.Clone();
        (clone.Chrome ?? new WorkspaceChromeLayout()).Apply(Settings, Columns);
        await ApplyLayoutAsync(clone.Layout, cancellationToken).ConfigureAwait(false);
        await _profiles.SetActiveAsync(clone.Id, cancellationToken).ConfigureAwait(false);
        await SaveWorkspaceLayoutAsync(cancellationToken).ConfigureAwait(false);
        await SaveUiSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<string> ExportWorkspaceProfileAsync(
        string id,
        CancellationToken cancellationToken = default)
        => _profiles.ExportAsync(id, cancellationToken);


    public Task<IReadOnlyList<WorkspaceProfile>> ListWorkspaceProfilesAsync(
        CancellationToken cancellationToken = default)
        => _profiles.ListAsync(cancellationToken);


}

