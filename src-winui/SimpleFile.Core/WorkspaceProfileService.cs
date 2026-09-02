namespace SimpleFile.Core;

internal sealed class WorkspaceProfileService
{
    private readonly Func<ISettingsBackend?> _fileOps;
    private readonly Func<string> _homePath;
    private readonly Func<WorkspaceLayout> _captureLayout;
    private readonly Func<WorkspaceChromeLayout> _captureChromeLayout;
    private readonly Action<string> _setActiveProfileId;

    public WorkspaceProfileService(
        Func<ISettingsBackend?> fileOps,
        Func<string> homePath,
        Func<WorkspaceLayout> captureLayout,
        Func<WorkspaceChromeLayout> captureChromeLayout,
        Action<string> setActiveProfileId)
    {
        _fileOps = fileOps;
        _homePath = homePath;
        _captureLayout = captureLayout;
        _captureChromeLayout = captureChromeLayout;
        _setActiveProfileId = setActiveProfileId;
    }

    public async Task<IReadOnlyList<WorkspaceProfile>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        _setActiveProfileId(document.ActiveProfileId);
        var builtIns = WorkspaceProfileTemplates.All(_homePath()).Select(profile => profile.Clone()).ToList();
        var custom = document.Profiles
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .Select(profile => profile.Clone())
            .ToList();
        return [.. builtIns, .. custom];
    }

    public async Task<WorkspaceProfile> SaveAsync(
        string name,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = WorkspaceProfilesDocument.NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("Profile name cannot be empty.", nameof(name));
        }

        var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var builtIn = WorkspaceProfileTemplates.All(_homePath()).FirstOrDefault(profile =>
            string.Equals(profile.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
        if (builtIn is not null)
        {
            throw new InvalidOperationException($"A built-in profile named \"{normalizedName}\" already exists.");
        }

        var existing = document.FindByName(normalizedName);
        if (existing is not null && !overwrite)
        {
            throw new InvalidOperationException($"A profile named \"{normalizedName}\" already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var saved = existing ?? new WorkspaceProfile
        {
            Id = WorkspaceProfilesDocument.NewId(),
            CreatedAt = now,
        };

        saved.Name = normalizedName;
        saved.UpdatedAt = now;
        saved.Layout = _captureLayout();
        saved.Chrome = _captureChromeLayout();
        if (existing is null)
        {
            document.Profiles.Add(saved);
        }

        document.ActiveProfileId = saved.Id;
        await SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        _setActiveProfileId(saved.Id);
        return saved.Clone();
    }

    public async Task<WorkspaceProfile> OverwriteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var saved = document.FindById(id)
            ?? throw new KeyNotFoundException("Profile was not found.");
        saved.UpdatedAt = DateTimeOffset.UtcNow;
        saved.Layout = _captureLayout();
        saved.Chrome = _captureChromeLayout();
        document.ActiveProfileId = saved.Id;
        await SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        _setActiveProfileId(saved.Id);
        return saved.Clone();
    }

    public async Task<WorkspaceProfile> DuplicateAsync(
        string id,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        var profiles = await ListAsync(cancellationToken).ConfigureAwait(false);
        var source = profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("Profile was not found.");

        var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var cloneName = UniqueName(
            string.IsNullOrWhiteSpace(name) ? $"{source.Name} copy" : name,
            profiles);
        var clone = source.CloneAsUser(cloneName);
        if (!source.IsBuiltIn && string.IsNullOrWhiteSpace(clone.SourceProfileId))
        {
            clone.SourceProfileId = source.Id;
        }

        document.Profiles.Add(clone);
        document.ActiveProfileId = clone.Id;
        await SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        _setActiveProfileId(clone.Id);
        return clone.Clone();
    }

    public async Task<WorkspaceProfile> RenameAsync(
        string id,
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = WorkspaceProfilesDocument.NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("Profile name cannot be empty.", nameof(name));
        }

        if (WorkspaceProfileTemplates.IsBuiltInId(id))
        {
            throw new InvalidOperationException("Built-in profiles cannot be renamed. Duplicate it first.");
        }

        var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var saved = document.FindById(id)
            ?? throw new KeyNotFoundException("Profile was not found.");
        var profiles = await ListAsync(cancellationToken).ConfigureAwait(false);
        if (profiles.Any(profile =>
                !string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(profile.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A profile named \"{normalizedName}\" already exists.");
        }

        saved.Name = normalizedName;
        saved.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        return saved.Clone();
    }

    public async Task<WorkspaceProfile> ResetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (WorkspaceProfileTemplates.Find(id, _homePath()) is { } builtIn)
        {
            return builtIn.Clone();
        }

        var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var saved = document.FindById(id)
            ?? throw new KeyNotFoundException("Profile was not found.");
        if (string.IsNullOrWhiteSpace(saved.SourceProfileId))
        {
            throw new InvalidOperationException("This profile does not have a reset source.");
        }

        var source = await FindAsync(saved.SourceProfileId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The reset source profile was not found.");
        var sourceClone = source.Clone();
        saved.Layout = sourceClone.Layout;
        saved.Chrome = sourceClone.Chrome;
        saved.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        return saved.Clone();
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (WorkspaceProfileTemplates.IsBuiltInId(id))
        {
            throw new InvalidOperationException("Built-in profiles cannot be deleted.");
        }

        var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var saved = document.FindById(id)
            ?? throw new KeyNotFoundException("Profile was not found.");
        document.Profiles.Remove(saved);
        if (string.Equals(document.ActiveProfileId, saved.Id, StringComparison.OrdinalIgnoreCase))
        {
            document.ActiveProfileId = "";
        }

        await SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        _setActiveProfileId(document.ActiveProfileId);
    }

    public async Task<WorkspaceProfile?> FindAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (WorkspaceProfileTemplates.Find(id, _homePath()) is { } builtIn)
        {
            return builtIn;
        }

        var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        return document.FindById(id);
    }

    public async Task SetActiveAsync(string id, CancellationToken cancellationToken = default)
    {
        _setActiveProfileId(id);
        if (_fileOps() is not { } fileOps)
        {
            return;
        }

        var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        document.ActiveProfileId = id;
        await SaveDocumentAsync(fileOps, document, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExportAsync(string id, CancellationToken cancellationToken = default)
    {
        var profile = await FindAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Profile was not found.");
        return WorkspaceProfileExportDocument.ToJson(profile);
    }

    private async Task<WorkspaceProfilesDocument> LoadDocumentAsync(
        CancellationToken cancellationToken)
    {
        if (_fileOps() is not { } fileOps)
        {
            return new WorkspaceProfilesDocument();
        }

        var json = await fileOps.GetSettingAsync(
            WorkspaceProfilesDocument.SettingsKey,
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(json))
        {
            return WorkspaceProfilesDocument.FromJson(json);
        }

        var legacyJson = await fileOps.GetSettingAsync(
            SavedWorkspaceLayoutsDocument.SettingsKey,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(legacyJson))
        {
            return new WorkspaceProfilesDocument();
        }

        var migrated = WorkspaceProfilesDocument.FromLegacyLayouts(
            SavedWorkspaceLayoutsDocument.FromJson(legacyJson));
        await SaveDocumentAsync(fileOps, migrated, cancellationToken).ConfigureAwait(false);
        return migrated;
    }

    private async Task SaveDocumentAsync(
        WorkspaceProfilesDocument document,
        CancellationToken cancellationToken)
    {
        if (_fileOps() is not { } fileOps)
        {
            throw new InvalidOperationException("Settings service is required for workspace profiles.");
        }

        await SaveDocumentAsync(fileOps, document, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveDocumentAsync(
        ISettingsBackend fileOps,
        WorkspaceProfilesDocument document,
        CancellationToken cancellationToken)
    {
        await fileOps.SetSettingAsync(
            WorkspaceProfilesDocument.SettingsKey,
            document.ToJson(),
            cancellationToken).ConfigureAwait(false);
    }

    private static string UniqueName(
        string? requested,
        IReadOnlyList<WorkspaceProfile> profiles)
    {
        var baseName = WorkspaceProfilesDocument.NormalizeName(requested);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "Profile";
        }

        var used = profiles.Select(profile => profile.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(baseName))
        {
            return baseName;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = $"{baseName} {index}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName} copy";
    }
}
