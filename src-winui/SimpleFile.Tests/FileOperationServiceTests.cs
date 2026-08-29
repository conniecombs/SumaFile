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
    [Fact]
    public async Task CreateFolderAsync_ReturnsPathFromIpc()
    {
        var stub = new ConfigurableIpc
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
        var stub = new ConfigurableIpc
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
        var first = new ConfigurableIpc
        {
            CreateDirectoryHandler = (path, name, ct) => Task.FromResult($@"{path}\old-{name}"),
        };
        var second = new ConfigurableIpc
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
        var stub = new ConfigurableIpc
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
        var stub = new ConfigurableIpc
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
        var stub = new ConfigurableIpc
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
        var stub = new ConfigurableIpc();
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
        var first = new ConfigurableIpc();
        var second = new ConfigurableIpc();
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
        var stub = new ConfigurableIpc();
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
        var stub = new ConfigurableIpc
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
    public async Task CalculateFolderSizeAsync_CancellationSendsBackendCancel()
    {
        using var cts = new CancellationTokenSource();
        var stub = new ConfigurableIpc
        {
            CalculateFolderSizeHandler = (_, ct) =>
            {
                cts.Cancel();
                return Task.FromCanceled<ulong>(ct);
            },
        };
        var service = new FileOperationService(stub);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CalculateFolderSizeAsync(@"S:\Movies", cts.Token));

        Assert.Equal(1, stub.CancelFolderSizeCalls);
    }

    [Fact]
    public async Task CountFolderItemsAsync_CancellationSendsBackendCancel()
    {
        using var cts = new CancellationTokenSource();
        var stub = new ConfigurableIpc
        {
            CountFolderItemsHandler = (_, ct) =>
            {
                cts.Cancel();
                return Task.FromCanceled<ulong>(ct);
            },
        };
        var service = new FileOperationService(stub);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CountFolderItemsAsync(@"S:\Movies", cts.Token));

        Assert.Equal(1, stub.CancelFolderItemCountCalls);
    }

    [Fact]
    public async Task CopyAsync_TokenCancel_CallsBackendCancel()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelRequested = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? copyOperationId = null;
        var stub = new ConfigurableIpc
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
    public async Task CopyAsync_ReturnedPartialResultAfterTokenCancel_IsCancellation()
    {
        var journal = TempJournal();
        using var cts = new CancellationTokenSource();
        var stub = new ConfigurableIpc
        {
            CopyWithProgressHandler = (sources, destination, operationId, conflictAction, ct) =>
            {
                cts.Cancel();
                return Task.FromResult(new[]
                {
                    new TransferResult { Source = sources[0], Destination = Path.Combine(destination, "a.txt") },
                });
            },
        };
        var service = new FileOperationService(stub, journal);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CopyAsync([@"C:\a.txt"], @"C:\dest", "skip", ct: cts.Token));

        var entries = journal.ReadEntries();
        Assert.Contains(entries, entry => entry.OperationType == "copy" && entry.State == "cancelled");
        Assert.DoesNotContain(entries, entry => entry.OperationType == "copy" && entry.State == "completed");
    }

    [Fact]
    public async Task MoveAsync_TokenCancel_CallsBackendCancel()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelRequested = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? moveOperationId = null;
        var stub = new ConfigurableIpc
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
        var stub = new ConfigurableIpc
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
        var stub = new ConfigurableIpc();
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
        var stub = new ConfigurableIpc();
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
        var stub = new ConfigurableIpc();
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
        var stub = new ConfigurableIpc
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
        var stub = new ConfigurableIpc
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
        var stub = new ConfigurableIpc
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
        var stub = new ConfigurableIpc
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
        var stub = new ConfigurableIpc
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
