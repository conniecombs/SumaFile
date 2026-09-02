using SimpleFile.Ipc;

namespace SimpleFile.Core;

public interface ISettingsBackend
{
    Task<string?> GetSettingAsync(string key, CancellationToken ct = default);
    Task SetSettingAsync(string key, string value, CancellationToken ct = default);
}

public interface ITagBackend
{
    Task<Tag[]> GetAllTagsAsync(CancellationToken ct = default);
    Task<Tag> CreateTagAsync(string name, string color, CancellationToken ct = default);
    Task UpdateTagAsync(long id, string name, string color, CancellationToken ct = default);
    Task DeleteTagAsync(long id, CancellationToken ct = default);
    Task<Tag[]> GetTagsForPathAsync(string path, CancellationToken ct = default);
    Task SetTagsForPathAsync(string path, long[] tagIds, CancellationToken ct = default);
    Task<Dictionary<string, Tag>> GetAllFileTagsAsync(CancellationToken ct = default);
    Task<string[]> GetFilesWithTagAsync(long tagId, CancellationToken ct = default);
}

public interface ISmartFolderBackend
{
    Task<SmartFolder[]> LoadSmartFoldersAsync(CancellationToken ct = default);
    Task<SmartFolder[]> SaveSmartFolderAsync(SmartFolder folder, CancellationToken ct = default);
    Task<SmartFolder[]> DeleteSmartFolderAsync(string id, CancellationToken ct = default);
}

public interface IFileOperationBackend
{
    Task<string> CreateFolderAsync(string path, string name, CancellationToken ct = default);
    Task<string> CreateFileAsync(string path, string name, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);
    Task<string[]> TrashAsync(string[] paths, CancellationToken ct = default);
    Task<string[]> RestoreRecycleBinAsync(string[] paths, CancellationToken ct = default);
    Task EmptyRecycleBinAsync(CancellationToken ct = default);
    Task<string> RenameAsync(string path, string newName, CancellationToken ct = default);
    Task<string[]> BatchRenameAsync(RenameRequest[] entries, CancellationToken ct = default);
    Task<TransferResult[]> CopyAsync(
        string[] sources,
        string destination,
        string conflictAction,
        IProgress<ProgressUpdate>? progress = null,
        Action<string>? operationStarted = null,
        CancellationToken ct = default);
    Task<TransferResult[]> MoveAsync(
        string[] sources,
        string destination,
        string conflictAction,
        IProgress<ProgressUpdate>? progress = null,
        Action<string>? operationStarted = null,
        CancellationToken ct = default);
    Task OpenFileAsync(string path, CancellationToken ct = default);
    Task RevealInFolderAsync(string path, CancellationToken ct = default);
}
