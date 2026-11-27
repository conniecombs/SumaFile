using System.Text.Json;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// The line that ends a pass in operations.jsonl says how long it took, how much it transferred and, for a
/// failed pass, every item that failed. It used to carry the first failure's message and nothing else, and
/// the file was never trimmed.
/// </summary>
public sealed class OperationJournalDetailTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sumafile-journal-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        foreach (var path in new[] { _path, _path + ".1" })
        {
            try { File.Delete(path); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task ACompletedPassRecordsItsDurationAndWhatItTransferred()
    {
        var journal = new OperationJournal(_path);
        var ipc = new ConfigurableIpc();
        ipc.CopyWithProgressHandler = (sources, destination, operationId, _, _) =>
        {
            ipc.Emit(Protocol.OperationProgressEvent, new ProgressUpdate
            {
                OperationId = operationId!, Current = 10, Total = 300, CurrentFiles = 1, TotalFiles = 3, Status = "running",
            });
            ipc.Emit(Protocol.OperationProgressEvent, new ProgressUpdate
            {
                OperationId = "someone-else", Current = 999, Total = 999, CurrentFiles = 9, TotalFiles = 9, Status = "running",
            });
            ipc.Emit(Protocol.OperationProgressEvent, new ProgressUpdate
            {
                OperationId = operationId!, Current = 300, Total = 300, CurrentFiles = 3, TotalFiles = 3, Status = "completed",
            });
            return Task.FromResult(new[]
            {
                new TransferResult { Source = sources[0], Destination = Path.Combine(destination, "a") },
                new TransferResult { Source = sources[1], Destination = Path.Combine(destination, "b") },
            });
        };

        await new FileOperationService(ipc, journal).CopyAsync([@"C:\a", @"C:\b"], @"C:\dest", "replace");

        var completed = journal.ReadEntries().Single(entry => entry.State == "completed");
        Assert.NotNull(completed.DurationMs);
        Assert.True(completed.DurationMs >= 0);
        Assert.Equal(300UL, completed.Bytes);
        Assert.Equal(300UL, completed.TotalBytes);
        Assert.Equal(3UL, completed.Files);
        Assert.Equal(3UL, completed.TotalFiles);
        Assert.Equal(2, completed.Items);
        Assert.Null(completed.Failures);
        Assert.Null(completed.FailureCount);
    }

    [Fact]
    public async Task AFailedPassRecordsEveryItemThatFailed()
    {
        var journal = new OperationJournal(_path);
        var ipc = new ConfigurableIpc();
        ipc.CopyWithProgressHandler = (sources, destination, operationId, _, _) =>
        {
            var outcome = new TransferBatchOutcome
            {
                OperationId = operationId!,
                Status = "failed",
                Committed = [new TransferResult { Source = sources[0], Destination = Path.Combine(destination, "a") }],
                Skipped = [new TransferResult { Source = sources[1], Destination = Path.Combine(destination, "b") }],
                Failed =
                [
                    new TransferFailure { Source = @"C:\c", Destination = @"C:\dest\c", Kind = "error", Error = "Read source: timed out" },
                    new TransferFailure { Source = @"C:\d", Destination = @"C:\dest\d", Kind = "conflict", Error = "CONFLICT: destination already exists" },
                ],
            };
            throw new IpcException(
                Protocol.ErrApplication,
                "Read source: timed out",
                JsonSerializer.SerializeToElement(outcome));
        };

        await Assert.ThrowsAsync<TransferOperationException>(() =>
            new FileOperationService(ipc, journal).CopyAsync([@"C:\a", @"C:\b", @"C:\c", @"C:\d"], @"C:\dest", "error"));

        var failed = journal.ReadEntries().Single(entry => entry.State == "failed");
        Assert.Equal("Read source: timed out", failed.Error);
        Assert.Equal(1, failed.Items);
        Assert.Equal(1, failed.Skipped);
        Assert.Equal(2, failed.FailureCount);
        Assert.NotNull(failed.Failures);
        Assert.Equal([@"C:\c", @"C:\d"], failed.Failures!.Select(failure => failure.Source));
        Assert.Equal([@"C:\dest\c", @"C:\dest\d"], failed.Failures!.Select(failure => failure.Destination));
        Assert.Equal(["error", "conflict"], failed.Failures!.Select(failure => failure.Kind));
        Assert.Equal("CONFLICT: destination already exists", failed.Failures![1].Error);
        Assert.NotNull(failed.DurationMs);
    }

    [Fact]
    public void ALineKeepsTheFirstFailuresAndTheCountOfAll()
    {
        var journal = new OperationJournal(_path);
        var failures = Enumerable.Range(0, OperationJournal.MaxFailuresPerEntry + 50)
            .Select(index => new TransferFailure { Source = $@"C:\src\{index}", Destination = $@"C:\dst\{index}", Error = "failed" })
            .ToArray();

        journal.Failed("copy", "op", new IOException("failed"), new OperationSummary { Failures = failures });

        var entry = journal.ReadEntries().Single();
        Assert.Equal(OperationJournal.MaxFailuresPerEntry, entry.Failures!.Length);
        Assert.Equal(failures.Length, entry.FailureCount);
        Assert.Equal(@"C:\src\0", entry.Failures[0].Source);
    }

    [Fact]
    public void ALineWithoutASummaryLooksAsItAlwaysDid()
    {
        var journal = new OperationJournal(_path);

        journal.Completed("scan", "op");

        var line = File.ReadAllLines(_path).Single();
        using var json = JsonDocument.Parse(line);
        Assert.Equal(
            ["timestamp", "operation_type", "operation_id", "state", "sources", "destination", "error"],
            json.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void TheJournalStartsAgainWhenItHasGrownLarge()
    {
        var journal = new OperationJournal(_path, maxBytes: 2_000);
        for (var index = 0; index < 100; index++)
        {
            journal.Started("copy", $"op-{index}", [@"C:\a-reasonably-long-source-path\file.bin"], @"C:\dest");
        }

        Assert.True(new FileInfo(_path).Length <= 2_000 + 400, "the journal was not started again");
        Assert.True(File.Exists(_path + ".1"), "the earlier lines were not kept beside it");
        Assert.True(new FileInfo(_path + ".1").Length <= 2_000 + 400);
        // The newest line is always in the journal itself.
        Assert.Equal("op-99", journal.ReadEntries()[^1].OperationId);
    }

    /// <summary>
    /// B7: when the full journal cannot be set aside (the kept file is read-only or locked, or another instance
    /// holds the journal), the line is added to it all the same, and the failure is logged once. Every line was
    /// dropped from then on.
    /// </summary>
    [Fact]
    public void ALineIsWrittenAllTheSameWhenTheJournalCannotStartAgain()
    {
        var logged = new List<string>();
        var journal = new OperationJournal(_path, maxBytes: 2_000, logged.Add);
        // A folder holds the name the earlier lines are kept under, so they cannot be moved there.
        Directory.CreateDirectory(_path + ".1");
        try
        {
            for (var index = 0; index < 50; index++)
            {
                journal.Started("copy", $"op-{index}", [@"C:\a-reasonably-long-source-path\file.bin"], @"C:\dest");
            }

            var entries = journal.ReadEntries();
            Assert.Equal(50, entries.Count);
            Assert.Equal("op-49", entries[^1].OperationId);
            Assert.Single(logged);
        }
        finally
        {
            Directory.Delete(_path + ".1", recursive: true);
        }
    }
}
