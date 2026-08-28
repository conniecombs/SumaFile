using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

public class FileOperationServiceTests
{
    private class StubIpc : ISimpleFileIpc
    {
        public Func<string, string, CancellationToken, Task<string>>? CreateDirectoryHandler;
        public Func<string, string, CancellationToken, Task<string>>? CreateFileHandler;
        public Func<string, string, CancellationToken, Task<string>>? RenameEntryHandler;
        public Func<string, CancellationToken, Task<string?>>? GetDbSettingHandler { get; set; }
        public Func<string, string, CancellationToken, Task>? SetDbSettingHandler { get; set; }
        public Func<string[], CancellationToken, Task>? MoveToTrashHandler;
        public Func<string[], string, string?, string, CancellationToken, Task<TransferResult[]>>? CopyWithProgressHandler;
        public Func<string[], string, string?, string, CancellationToken, Task<TransferResult[]>>? MoveWithProgressHandler;
        public Func<string, CancellationToken, Task>? CancelOperationHandler { get; set; }
        public Func<SearchOptions, Action<SearchResult[]>?, Action<int>?, CancellationToken, Task<SearchResult[]>>? SearchFilesHandler { get; set; }
        public Func<string, CancellationToken, Task>? CancelSearchHandler { get; set; }
        public Func<string, CancellationToken, Task>? WatchDirectoryHandler { get; set; }
        public Func<CancellationToken, Task>? UnwatchDirectoryHandler { get; set; }
        public Func<string, ulong?, CancellationToken, Task<FilePreview>>? ReadFilePreviewHandler { get; set; }
        public Func<string, CancellationToken, Task<Checksums>>? ComputeChecksumHandler { get; set; }
        public Func<string, CancellationToken, Task<FileMetadata>>? GetFileMetadataHandler { get; set; }
        public Func<string, string, CancellationToken, Task<FileComparison>>? CompareFilesHandler { get; set; }
        public Func<string, CancellationToken, Task<ArchiveInfo>>? ListArchiveHandler { get; set; }
        public Func<string, string, CancellationToken, Task>? ExtractArchiveHandler { get; set; }
        public Func<string[], string, string, CancellationToken, Task>? CreateArchiveHandler { get; set; }
        public Func<string, ulong?, string?, CancellationToken, Task<CleanupResult>>? DiskCleanupHandler { get; set; }
        public Func<string, ulong?, ulong?, string?, CancellationToken, Task<DuplicateCheckResult>>? DuplicateCheckHandler { get; set; }
        public Func<CancellationToken, Task>? InstallUpdateHandler { get; set; }
        private readonly Dictionary<string, List<object>> _handlers = new();

        public Task<string> CreateDirectoryAsync(string path, string name, CancellationToken ct = default)
            => CreateDirectoryHandler?.Invoke(path, name, ct) ?? throw new NotImplementedException();

        public Task<string> CreateFileAsync(string path, string name, CancellationToken ct = default)
            => CreateFileHandler?.Invoke(path, name, ct) ?? throw new NotImplementedException();

        public Task<string> RenameEntryAsync(string path, string newName, CancellationToken ct = default)
            => RenameEntryHandler?.Invoke(path, newName, ct) ?? throw new NotImplementedException();

        public Task<string?> GetDbSettingAsync(string key, CancellationToken ct = default)
            => GetDbSettingHandler?.Invoke(key, ct) ?? throw new NotImplementedException();

        public Task SetDbSettingAsync(string key, string value, CancellationToken ct = default)
            => SetDbSettingHandler?.Invoke(key, value, ct) ?? throw new NotImplementedException();

        public Task MoveToTrashAsync(string[] paths, CancellationToken ct = default)
        {
            if (MoveToTrashHandler != null)
                return MoveToTrashHandler(paths, ct);
            throw new NotImplementedException();
        }

        public Task<TransferResult[]> CopyWithProgressAsync(string[] sources, string destination, string? operationId, string conflictAction, CancellationToken ct = default)
            => CopyWithProgressHandler?.Invoke(sources, destination, operationId, conflictAction, ct) ?? throw new NotImplementedException();

        public Task CancelOperationAsync(string operationId, CancellationToken ct = default)
            => CancelOperationHandler?.Invoke(operationId, ct) ?? throw new NotImplementedException();

        public Task<SearchResult[]> SearchFilesAsync(SearchOptions options, Action<SearchResult[]>? onBatch = null, Action<int>? onComplete = null, CancellationToken ct = default)
            => SearchFilesHandler?.Invoke(options, onBatch, onComplete, ct) ?? throw new NotImplementedException();

        public Task CancelSearchAsync(string searchId, CancellationToken ct = default)
            => CancelSearchHandler?.Invoke(searchId, ct) ?? throw new NotImplementedException();

        public Task WatchDirectoryAsync(string path, CancellationToken ct = default)
            => WatchDirectoryHandler?.Invoke(path, ct) ?? throw new NotImplementedException();

        public Task UnwatchDirectoryAsync(CancellationToken ct = default)
            => UnwatchDirectoryHandler?.Invoke(ct) ?? throw new NotImplementedException();

        public Task<FilePreview> ReadFilePreviewAsync(string path, ulong? maxSize = null, CancellationToken ct = default)
            => ReadFilePreviewHandler?.Invoke(path, maxSize, ct) ?? throw new NotImplementedException();

        public Task<Checksums> ComputeChecksumAsync(string path, CancellationToken ct = default)
            => ComputeChecksumHandler?.Invoke(path, ct) ?? throw new NotImplementedException();

        public Task<FileMetadata> GetFileMetadataAsync(string path, CancellationToken ct = default)
            => GetFileMetadataHandler?.Invoke(path, ct) ?? throw new NotImplementedException();

        public Task<FileComparison> CompareFilesAsync(string pathA, string pathB, CancellationToken ct = default)
            => CompareFilesHandler?.Invoke(pathA, pathB, ct) ?? throw new NotImplementedException();

        public Task<ArchiveInfo> ListArchiveAsync(string path, CancellationToken ct = default)
            => ListArchiveHandler?.Invoke(path, ct) ?? throw new NotImplementedException();

        public Task ExtractArchiveAsync(string archivePath, string destination, CancellationToken ct = default)
            => ExtractArchiveHandler?.Invoke(archivePath, destination, ct) ?? throw new NotImplementedException();

        public Task CreateArchiveAsync(string[] paths, string archivePath, string format, CancellationToken ct = default)
            => CreateArchiveHandler?.Invoke(paths, archivePath, format, ct) ?? throw new NotImplementedException();

        public Task<CleanupResult> DiskCleanupAsync(string path, ulong? minSize, string? opId, CancellationToken ct = default)
            => DiskCleanupHandler?.Invoke(path, minSize, opId, ct) ?? throw new NotImplementedException();

        public Task<DuplicateCheckResult> DuplicateCheckAsync(string path, ulong? minSize, ulong? hashBytes, string? opId, CancellationToken ct = default)
            => DuplicateCheckHandler?.Invoke(path, minSize, hashBytes, opId, ct) ?? throw new NotImplementedException();

        public int SubscriptionCount(string eventName)
            => _handlers.TryGetValue(eventName, out var handlers) ? handlers.Count : 0;

        public void Emit<T>(string eventName, T payload)
        {
            if (!_handlers.TryGetValue(eventName, out var handlers)) return;
            foreach (var handler in handlers.OfType<Action<T>>().ToArray())
            {
                handler(payload);
            }
        }

        // Dummy implementations for the rest
        public bool IsConnected => throw new NotImplementedException();
#pragma warning disable CS0067
        public event EventHandler<Exception?>? Disconnected;
#pragma warning restore CS0067
        public Task<HandshakeResult> HandshakeAsync(string authToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<TResult> InvokeAsync<TResult>(string method, object? args, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task InvokeAsync(string method, object? args, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IDisposable On<T>(string eventName, Action<T> handler)
        {
            if (!_handlers.TryGetValue(eventName, out var handlers))
            {
                handlers = new List<object>();
                _handlers[eventName] = handlers;
            }
            handlers.Add(handler);
            return new TestSubscription(() => handlers.Remove(handler));
        }
        public Task<DirectoryListing> ListDirectoryAsync(string path, Action<DirectoryListingChunk>? onChunk = null, CancellationToken cancellationToken = default, ListDirectoryOptions? options = null) => throw new NotImplementedException();
        public Task<HealthResult> HealthAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> GetAppVersionAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> GetHomeDirAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<DriveInfo>> ListDrivesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SelectDirectoryAsync(string? defaultPath = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ShowMainWindowAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteEntryAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string[]> BatchRenameAsync(RenameRequest[] entries, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> CopyEntryAsync(string source, string destination, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> MoveEntryAsync(string source, string destination, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> CopyEntryResolvedAsync(string source, string destination, string conflictAction, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> MoveEntryResolvedAsync(string source, string destination, string conflictAction, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FileEntry> GetEntryInfoAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task OpenFileAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RevealInFolderAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task OpenExternalUrlAsync(string url, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GenerateThumbnailAsync(string path, uint size, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ThumbnailResult[]> GenerateThumbnailsAsync(string[] paths, uint size, CancellationToken ct = default) => throw new NotImplementedException();
        public Task OpenFileWithAsync(string path, string application, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ImageMetadata> GetImageMetadataAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TreeNode[]> ListSubdirectoriesAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ulong> CalculateFolderSizeAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ulong> CountFolderItemsAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TransferResult[]> MoveWithProgressAsync(string[] sources, string destination, string? operationId, string conflictAction, CancellationToken ct = default)
            => MoveWithProgressHandler?.Invoke(sources, destination, operationId, conflictAction, ct) ?? throw new NotImplementedException();
        public Task OpenTerminalAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task OpenPowershellAdminAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitStatus> GetGitStatusAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FileEntry[]> GetGitFileStatusesAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task GitPullAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task GitPushAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SmartFolder[]> LoadSmartFoldersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SmartFolder[]> SaveSmartFolderAsync(SmartFolder folder, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SmartFolder[]> DeleteSmartFolderAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AppAboutInfo> GetAppAboutInfoAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task InstallUpdateAsync(CancellationToken ct = default)
            => InstallUpdateHandler?.Invoke(ct) ?? throw new NotImplementedException();
        public Task CancelFolderSizeAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task CancelFolderItemCountAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task CancelCountItemsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> CheckRarInstalledAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RarInstallPlan> PrepareRarInstallAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task DiscardRarInstallAsync(string confirmationToken, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> InstallRarAsync(string confirmationToken, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CancelDiskCleanupAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task CancelDuplicateCheckAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Tag[]> GetAllTagsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Tag> CreateTagAsync(string name, string color, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Tag> UpdateTagAsync(long id, string name, string color, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteTagAsync(long id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Tag[]> GetTagsForPathAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetTagsForPathAsync(string path, long[] tags, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Dictionary<string, Tag>> GetAllFileTagsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string[]> GetFilesWithTagAsync(long id, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestSubscription : IDisposable
    {
        private readonly Action _dispose;
        private bool _disposed;

        public TestSubscription(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _dispose();
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public InlineProgress(Action<T> report)
        {
            _report = report;
        }

        public void Report(T value) => _report(value);
    }

    [Fact]
    public async Task CreateFolderAsync_ReturnsPathFromIpc()
    {
        var stub = new StubIpc
        {
            CreateDirectoryHandler = (path, name, ct) => Task.FromResult($@"{path}\{name}")
        };
        var service = new FileOperationService(stub);

        var result = await service.CreateFolderAsync(@"C:\test", "newfolder");

        Assert.Equal(@"C:\test\newfolder", result);
    }

    [Fact]
    public async Task CreateFileAsync_ReturnsPathFromIpc()
    {
        var stub = new StubIpc
        {
            CreateFileHandler = (path, name, ct) => Task.FromResult($@"{path}\{name}")
        };
        var service = new FileOperationService(stub);

        var result = await service.CreateFileAsync(@"C:\test", "newfile.txt");

        Assert.Equal(@"C:\test\newfile.txt", result);
    }

    [Fact]
    public async Task ReplaceIpc_UsesNewClientForFutureCalls()
    {
        var first = new StubIpc
        {
            CreateDirectoryHandler = (path, name, ct) => Task.FromResult($@"{path}\old-{name}"),
        };
        var second = new StubIpc
        {
            CreateDirectoryHandler = (path, name, ct) => Task.FromResult($@"{path}\new-{name}"),
        };
        var service = new FileOperationService(first);

        var before = await service.CreateFolderAsync(@"C:\test", "folder");
        service.ReplaceIpc(second);
        var after = await service.CreateFolderAsync(@"C:\test", "folder");

        Assert.Equal(@"C:\test\old-folder", before);
        Assert.Equal(@"C:\test\new-folder", after);
    }

    [Fact]
    public async Task RenameAsync_ReturnsNewPathFromIpc()
    {
        var stub = new StubIpc
        {
            RenameEntryHandler = (path, newName, ct) => Task.FromResult($@"C:\test\{newName}")
        };
        var service = new FileOperationService(stub);

        var result = await service.RenameAsync(@"C:\test\old.txt", "new.txt");

        Assert.Equal(@"C:\test\new.txt", result);
    }

    [Fact]
    public async Task TrashAsync_CallsIpcWithCorrectPaths()
    {
        string[]? receivedPaths = null;
        var stub = new StubIpc
        {
            MoveToTrashHandler = (paths, ct) =>
            {
                receivedPaths = paths;
                return Task.CompletedTask;
            }
        };
        var service = new FileOperationService(stub);
        var inputPaths = new[] { @"C:\test\file1.txt", @"C:\test\file2.txt" };

        await service.TrashAsync(inputPaths);

        Assert.Equal(inputPaths, receivedPaths);
    }

    [Fact]
    public void IsConflict_CorrectlyDetectsConflictPrefix()
    {
        var conflictEx = new IpcException(Protocol.ErrApplication, "CONFLICT: file exists");
        var otherEx = new IpcException(Protocol.ErrApplication, "some other error");

        Assert.True(FileOperationService.IsConflict(conflictEx));
        Assert.False(FileOperationService.IsConflict(otherEx));
    }

    [Fact]
    public void IsTrashUnavailable_CorrectlyDetectsTrashUnavailablePrefix()
    {
        var trashEx = new IpcException(Protocol.ErrApplication, "TRASH_UNAVAILABLE: no trash on this drive");
        var otherEx = new IpcException(Protocol.ErrApplication, "some other error");

        Assert.True(FileOperationService.IsTrashUnavailable(trashEx));
        Assert.False(FileOperationService.IsTrashUnavailable(otherEx));
    }

    [Fact]
    public void TrashUnavailableMessage_HidesRawBackendDetails()
    {
        var error = new IpcException(
            Protocol.ErrApplication,
            "TRASH_UNAVAILABLE: Error during a 'trash' operation: Os { code: -2147024894, description: \"windows error: The system cannot find the file specified. (0x80070002)\" }");

        var message = FileOperationService.TrashUnavailableMessage(error);

        Assert.Contains("Recycle Bin", message);
        Assert.Contains("may no longer be available", message);
        Assert.Contains("location may not support", message);
        Assert.Contains("Delete Permanently", message);
        Assert.DoesNotContain("TRASH_UNAVAILABLE", message);
        Assert.DoesNotContain("-2147024894", message);
        Assert.DoesNotContain("0x80070002", message);
    }

    [Fact]
    public async Task GenerateOperationId_FormatCheck()
    {
        string? capturedOpId = null;
        var stub = new StubIpc
        {
            CopyWithProgressHandler = (sources, dest, opId, conflictAction, ct) =>
            {
                capturedOpId = opId;
                return Task.FromResult(Array.Empty<TransferResult>());
            }
        };
        var service = new FileOperationService(stub);

        await service.CopyAsync(new[] { "a" }, "b", "error");

        Assert.NotNull(capturedOpId);
        Assert.Matches(@"^op_\d+_\d+$", capturedOpId);
    }

    [Fact]
    public async Task CopyAsync_ReportsProgressAndDisposesSubscription()
    {
        var seen = new List<ProgressUpdate>();
        var stub = new StubIpc();
        stub.CopyWithProgressHandler = (sources, dest, opId, conflictAction, ct) =>
        {
            stub.Emit(
                Protocol.OperationProgressEvent,
                new ProgressUpdate
                {
                    OperationId = opId!,
                    OperationType = "copy",
                    Current = 1,
                    Total = 2,
                    Status = "running",
                });
            return Task.FromResult(Array.Empty<TransferResult>());
        };
        var service = new FileOperationService(stub);

        await service.CopyAsync(
            new[] { "a" },
            "b",
            "error",
            new InlineProgress<ProgressUpdate>(seen.Add));

        Assert.Single(seen);
        Assert.Equal(0, stub.SubscriptionCount(Protocol.OperationProgressEvent));

        stub.Emit(
            Protocol.OperationProgressEvent,
            new ProgressUpdate { OperationId = seen[0].OperationId, Status = "running" });
        Assert.Single(seen);
    }

    [Fact]
    public async Task CopyAsync_KeepsOriginalClientWhenIpcIsReplaced()
    {
        var first = new StubIpc();
        var second = new StubIpc();
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<TransferResult[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.CopyWithProgressHandler = (sources, dest, opId, conflictAction, ct) =>
        {
            started.SetResult(opId!);
            return finish.Task;
        };
        second.CopyWithProgressHandler = (sources, dest, opId, conflictAction, ct) =>
            throw new InvalidOperationException("In-flight copy switched IPC clients.");
        var service = new FileOperationService(first);

        var copy = service.CopyAsync(
            ["a"],
            "b",
            "error",
            new InlineProgress<ProgressUpdate>(_ => { }));
        var operationId = await started.Task;
        Assert.Equal(1, first.SubscriptionCount(Protocol.OperationProgressEvent));

        service.ReplaceIpc(second);
        finish.SetResult([]);
        await copy;

        Assert.Matches(@"^op_\d+_\d+$", operationId);
        Assert.Equal(0, first.SubscriptionCount(Protocol.OperationProgressEvent));
        Assert.Equal(0, second.SubscriptionCount(Protocol.OperationProgressEvent));
    }

    [Fact]
    public async Task CopyAsync_CancellationDisposesProgressSubscription()
    {
        var seen = new List<ProgressUpdate>();
        using var cts = new CancellationTokenSource();
        var stub = new StubIpc();
        stub.CopyWithProgressHandler = (sources, dest, opId, conflictAction, ct) =>
        {
            stub.Emit(
                Protocol.OperationProgressEvent,
                new ProgressUpdate
                {
                    OperationId = opId!,
                    OperationType = "copy",
                    Current = 1,
                    Total = 3,
                    Status = "running",
                });
            cts.Cancel();
            return Task.FromCanceled<TransferResult[]>(ct);
        };
        var service = new FileOperationService(stub);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CopyAsync(
                ["a"],
                "b",
                "error",
                new InlineProgress<ProgressUpdate>(seen.Add),
                ct: cts.Token));

        Assert.Single(seen);
        Assert.Equal(0, stub.SubscriptionCount(Protocol.OperationProgressEvent));
        stub.Emit(
            Protocol.OperationProgressEvent,
            new ProgressUpdate { OperationId = seen[0].OperationId, OperationType = "copy" });
        Assert.Single(seen);
    }

    [Fact]
    public async Task CancelOperationAsync_CallsNamedIpcCancel()
    {
        string? cancelled = null;
        var stub = new StubIpc
        {
            CancelOperationHandler = (operationId, ct) =>
            {
                cancelled = operationId;
                return Task.CompletedTask;
            }
        };
        var service = new FileOperationService(stub);

        await service.CancelOperationAsync("op_123_4");

        Assert.Equal("op_123_4", cancelled);
    }

    [Fact]
    public async Task CopyAsync_TokenCancel_CallsBackendCancel()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelRequested = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? copyOperationId = null;
        var stub = new StubIpc
        {
            CopyWithProgressHandler = async (sources, destination, operationId, conflictAction, ct) =>
            {
                copyOperationId = operationId;
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return Array.Empty<TransferResult>();
            },
            CancelOperationHandler = (operationId, ct) =>
            {
                cancelRequested.SetResult(operationId);
                return Task.CompletedTask;
            }
        };
        var service = new FileOperationService(stub);
        using var cts = new CancellationTokenSource();

        var copyTask = service.CopyAsync(
            [@"C:\a.txt"],
            @"C:\dest",
            "skip",
            ct: cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copyTask);
        var cancelled = await cancelRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(copyOperationId, cancelled);
        Assert.False(string.IsNullOrEmpty(cancelled));
    }

    [Fact]
    public async Task MoveAsync_TokenCancel_CallsBackendCancel()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelRequested = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? moveOperationId = null;
        var stub = new StubIpc
        {
            MoveWithProgressHandler = async (sources, destination, operationId, conflictAction, ct) =>
            {
                moveOperationId = operationId;
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return Array.Empty<TransferResult>();
            },
            CancelOperationHandler = (operationId, ct) =>
            {
                cancelRequested.SetResult(operationId);
                return Task.CompletedTask;
            }
        };
        var service = new FileOperationService(stub);
        using var cts = new CancellationTokenSource();

        var moveTask = service.MoveAsync(
            [@"C:\a.txt"],
            @"C:\dest",
            "skip",
            ct: cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveTask);
        var cancelled = await cancelRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(moveOperationId, cancelled);
        Assert.False(string.IsNullOrEmpty(cancelled));
    }

    [Fact]
    public async Task SearchAsync_StreamsEventsAndDisposesSubscriptions()
    {
        var batches = new List<SearchResult[]>();
        var completes = new List<int>();
        var stub = new StubIpc
        {
            SearchFilesHandler = (options, onBatch, onComplete, ct) =>
            {
                onBatch?.Invoke(new[] { new SearchResult { Name = "alpha.txt", Path = @"C:\alpha.txt" } });
                onComplete?.Invoke(1);
                return Task.FromResult(new[] { new SearchResult { Name = "alpha.txt", Path = @"C:\alpha.txt" } });
            }
        };
        var service = new FileOperationService(stub);

        var results = await service.SearchAsync(
            new SearchOptions { Query = "alpha", SearchPath = @"C:\" },
            batches.Add,
            completes.Add);

        Assert.Single(results);
        Assert.Single(batches);
        Assert.Equal([1], completes);
    }

    [Fact]
    public async Task DiskCleanupAsync_CancellationDisposesProgressSubscription()
    {
        var seen = new List<ProgressUpdate>();
        using var cts = new CancellationTokenSource();
        var stub = new StubIpc();
        stub.DiskCleanupHandler = (path, minSize, opId, ct) =>
        {
            stub.Emit(
                Protocol.OperationProgressEvent,
                new ProgressUpdate
                {
                    OperationId = opId!,
                    OperationType = "cleanup",
                    Current = 1,
                    Total = 10,
                    Status = "running",
                });
            cts.Cancel();
            return Task.FromCanceled<CleanupResult>(ct);
        };
        var service = new FileOperationService(stub);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DiskCleanupAsync(
                @"C:\test",
                1024,
                new InlineProgress<ProgressUpdate>(seen.Add),
                cts.Token));

        Assert.Single(seen);
        Assert.Equal(0, stub.SubscriptionCount(Protocol.OperationProgressEvent));
        stub.Emit(
            Protocol.OperationProgressEvent,
            new ProgressUpdate { OperationId = seen[0].OperationId, OperationType = "cleanup" });
        Assert.Single(seen);
    }

    [Fact]
    public async Task DuplicateCheckAsync_CancellationDisposesProgressSubscription()
    {
        var seen = new List<ProgressUpdate>();
        using var cts = new CancellationTokenSource();
        var stub = new StubIpc();
        stub.DuplicateCheckHandler = (path, minSize, hashBytes, opId, ct) =>
        {
            stub.Emit(
                Protocol.OperationProgressEvent,
                new ProgressUpdate
                {
                    OperationId = opId!,
                    OperationType = "duplicate-check",
                    Current = 1,
                    Total = 10,
                    Status = "running",
                });
            cts.Cancel();
            return Task.FromCanceled<DuplicateCheckResult>(ct);
        };
        var service = new FileOperationService(stub);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DuplicateCheckAsync(
                @"C:\test",
                1024,
                null,
                new InlineProgress<ProgressUpdate>(seen.Add),
                cts.Token));

        Assert.Single(seen);
        Assert.Equal(0, stub.SubscriptionCount(Protocol.OperationProgressEvent));
        stub.Emit(
            Protocol.OperationProgressEvent,
            new ProgressUpdate { OperationId = seen[0].OperationId, OperationType = "duplicate-check" });
        Assert.Single(seen);
    }

    [Fact]
    public async Task InstallUpdateAsync_ReportsProgressAndDisposesSubscription()
    {
        var seen = new List<long[]>();
        var stub = new StubIpc();
        stub.InstallUpdateHandler = ct =>
        {
            stub.Emit(Protocol.UpdateChunkEvent, new long[] { 42, 100 });
            return Task.CompletedTask;
        };
        var service = new FileOperationService(stub);

        await service.InstallUpdateAsync(new InlineProgress<long[]>(seen.Add));

        Assert.Single(seen);
        Assert.Equal(new long[] { 42, 100 }, seen[0]);
        Assert.Equal(0, stub.SubscriptionCount(Protocol.UpdateChunkEvent));

        stub.Emit(Protocol.UpdateChunkEvent, new long[] { 100, 100 });
        Assert.Single(seen);
    }

    [Fact]
    public async Task OperationJournal_RecordsTransferLifecycle()
    {
        var journal = TempJournal();
        var stub = new StubIpc
        {
            CopyWithProgressHandler = (sources, destination, operationId, conflictAction, ct) =>
            {
                Assert.NotNull(operationId);
                return Task.FromResult(new[]
                {
                    new TransferResult { Source = sources[0], Destination = Path.Combine(destination, "a.txt") },
                });
            },
        };
        var service = new FileOperationService(stub, journal);

        await service.CopyAsync([@"C:\a.txt"], @"C:\dest", "replace");

        var entries = journal.ReadEntries();
        Assert.Equal(["started", "completed"], entries.Select(entry => entry.State));
        Assert.All(entries, entry => Assert.Equal("copy", entry.OperationType));
        Assert.Equal(@"C:\a.txt", entries[0].Sources.Single());
        Assert.Equal(@"C:\dest", entries[0].Destination);
        Assert.Equal(entries[0].OperationId, entries[1].OperationId);
    }

    [Fact]
    public async Task OperationJournal_RecordsCancellationAndFailure()
    {
        var journal = TempJournal();
        using var cts = new CancellationTokenSource();
        var stub = new StubIpc
        {
            CopyWithProgressHandler = (sources, destination, operationId, conflictAction, ct) =>
            {
                cts.Cancel();
                return Task.FromCanceled<TransferResult[]>(ct);
            },
            CancelOperationHandler = (operationId, ct) => Task.CompletedTask,
            InstallUpdateHandler = ct => throw new InvalidOperationException("signature mismatch"),
        };
        var service = new FileOperationService(stub, journal);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CopyAsync([@"C:\a.txt"], @"C:\dest", "skip", ct: cts.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallUpdateAsync());

        var entries = journal.ReadEntries();
        Assert.Contains(entries, entry => entry.OperationType == "copy" && entry.State == "cancelled");
        var failedUpdate = Assert.Single(entries, entry => entry.OperationType == "update" && entry.State == "failed");
        Assert.Contains("signature mismatch", failedUpdate.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectionMethods_CallTypedIpc()
    {
        string? previewPath = null;
        ulong? previewSize = null;
        string? checksumPath = null;
        (string A, string B)? compared = null;
        var stub = new StubIpc
        {
            ReadFilePreviewHandler = (path, maxSize, ct) =>
            {
                previewPath = path;
                previewSize = maxSize;
                return Task.FromResult(new FilePreview
                {
                    FileType = "text",
                    MimeType = "text/plain",
                    Content = "hello",
                    Encoding = "utf-8",
                    Size = 5,
                });
            },
            ComputeChecksumHandler = (path, ct) =>
            {
                checksumPath = path;
                return Task.FromResult(new Checksums { Md5 = "m", Sha1 = "s1", Sha256 = "s256" });
            },
            CompareFilesHandler = (pathA, pathB, ct) =>
            {
                compared = (pathA, pathB);
                return Task.FromResult(new FileComparison { LeftPath = pathA, RightPath = pathB, Identical = true });
            },
        };
        var service = new FileOperationService(stub);

        var preview = await service.ReadFilePreviewAsync(@"C:\a.txt", 512);
        var checksums = await service.ComputeChecksumAsync(@"C:\a.txt");
        var comparison = await service.CompareFilesAsync(@"C:\a.txt", @"C:\b.txt");

        Assert.Equal("hello", preview.Content);
        Assert.Equal(@"C:\a.txt", previewPath);
        Assert.Equal(512ul, previewSize);
        Assert.Equal(@"C:\a.txt", checksumPath);
        Assert.Equal("s256", checksums.Sha256);
        Assert.Equal((@"C:\a.txt", @"C:\b.txt"), compared);
        Assert.True(comparison.Identical);
    }

    [Fact]
    public async Task ArchiveMethods_CallTypedIpc()
    {
        string? listed = null;
        (string Archive, string Destination)? extracted = null;
        (string[] Paths, string Archive, string Format)? created = null;
        var stub = new StubIpc
        {
            ListArchiveHandler = (path, ct) =>
            {
                listed = path;
                return Task.FromResult(new ArchiveInfo
                {
                    Path = path,
                    Format = "zip",
                    Entries =
                    [
                        new ArchiveEntry { Name = "notes.txt", Path = "notes.txt", Size = 5 },
                    ],
                    TotalSize = 5,
                    CompressedSize = 4,
                });
            },
            ExtractArchiveHandler = (archive, destination, ct) =>
            {
                extracted = (archive, destination);
                return Task.CompletedTask;
            },
            CreateArchiveHandler = (paths, archive, format, ct) =>
            {
                created = (paths, archive, format);
                return Task.CompletedTask;
            },
        };
        var service = new FileOperationService(stub);

        var info = await service.ListArchiveAsync(@"C:\pack.zip");
        await service.ExtractArchiveAsync(@"C:\pack.zip", @"C:\out");
        await service.CreateArchiveAsync([@"C:\a.txt", @"C:\b.txt"], @"C:\pack.zip", "zip");

        Assert.Equal(@"C:\pack.zip", listed);
        Assert.Equal("notes.txt", info.Entries[0].Name);
        Assert.Equal((@"C:\pack.zip", @"C:\out"), extracted);
        Assert.NotNull(created);
        Assert.Equal([@"C:\a.txt", @"C:\b.txt"], created.Value.Paths);
        Assert.Equal(@"C:\pack.zip", created.Value.Archive);
        Assert.Equal("zip", created.Value.Format);
    }

    [Fact]
    public async Task SettingsMethods_CallTypedIpc()
    {
        string? requestedKey = null;
        (string Key, string Value)? saved = null;
        var stub = new StubIpc
        {
            GetDbSettingHandler = (key, ct) =>
            {
                requestedKey = key;
                return Task.FromResult<string?>("{\"version\":1}");
            },
            SetDbSettingHandler = (key, value, ct) =>
            {
                saved = (key, value);
                return Task.CompletedTask;
            },
        };
        var service = new FileOperationService(stub);

        var value = await service.GetSettingAsync("winui.workspace.layout.v1");
        await service.SetSettingAsync("winui.workspace.layout.v1", "{\"version\":1}");

        Assert.Equal("winui.workspace.layout.v1", requestedKey);
        Assert.Equal("{\"version\":1}", value);
        Assert.Equal(("winui.workspace.layout.v1", "{\"version\":1}"), saved);
    }

    private static OperationJournal TempJournal()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"sumafile-operation-journal-{Guid.NewGuid():N}.jsonl");
        return new OperationJournal(path);
    }
}
