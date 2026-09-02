namespace SimpleFile.Core;

internal sealed class SavedWorkspaceLayoutService
{
    private readonly Func<ISettingsBackend?> _fileOps;
    private readonly Func<WorkspaceLayout> _captureLayout;
    private readonly Func<WorkspaceChromeLayout> _captureChromeLayout;

    public SavedWorkspaceLayoutService(
        Func<ISettingsBackend?> fileOps,
        Func<WorkspaceLayout> captureLayout,
        Func<WorkspaceChromeLayout> captureChromeLayout)
    {
        _fileOps = fileOps;
        _captureLayout = captureLayout;
        _captureChromeLayout = captureChromeLayout;
    }

    public async Task<IReadOnlyList<SavedWorkspaceLayout>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        return document.Layouts
            .OrderBy(layout => layout.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<SavedWorkspaceLayout> SaveAsync(
        string name,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = SavedWorkspaceLayoutsDocument.NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("Layout name cannot be empty.", nameof(name));
        }

        var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var existing = document.FindByName(normalizedName);
        if (existing is not null && !overwrite)
        {
            throw new InvalidOperationException($"A layout named \"{normalizedName}\" already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var saved = existing ?? new SavedWorkspaceLayout
        {
            Id = SavedWorkspaceLayoutsDocument.NewId(),
            CreatedAt = now,
        };

        saved.Name = normalizedName;
        saved.UpdatedAt = now;
        saved.Layout = _captureLayout();
        saved.Chrome = _captureChromeLayout();
        if (existing is null)
        {
            document.Layouts.Add(saved);
        }

        await SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public async Task<SavedWorkspaceLayout> OverwriteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var saved = document.FindById(id)
            ?? throw new KeyNotFoundException("Saved layout was not found.");
        saved.UpdatedAt = DateTimeOffset.UtcNow;
        saved.Layout = _captureLayout();
        saved.Chrome = _captureChromeLayout();
        await SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var saved = document.FindById(id)
            ?? throw new KeyNotFoundException("Saved layout was not found.");
        document.Layouts.Remove(saved);
        await SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SavedWorkspaceLayout?> FindAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        return document.FindById(id);
    }

    private async Task<SavedWorkspaceLayoutsDocument> LoadDocumentAsync(
        CancellationToken cancellationToken)
    {
        if (_fileOps() is not { } fileOps)
        {
            return new SavedWorkspaceLayoutsDocument();
        }

        var json = await fileOps.GetSettingAsync(
            SavedWorkspaceLayoutsDocument.SettingsKey,
            cancellationToken).ConfigureAwait(false);
        return SavedWorkspaceLayoutsDocument.FromJson(json);
    }

    private async Task SaveDocumentAsync(
        SavedWorkspaceLayoutsDocument document,
        CancellationToken cancellationToken)
    {
        if (_fileOps() is not { } fileOps)
        {
            throw new InvalidOperationException("Settings service is required for saved layouts.");
        }

        await fileOps.SetSettingAsync(
            SavedWorkspaceLayoutsDocument.SettingsKey,
            document.ToJson(),
            cancellationToken).ConfigureAwait(false);
    }
}
