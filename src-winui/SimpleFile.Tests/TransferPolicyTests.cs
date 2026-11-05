using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public sealed class TransferPolicyTests
{
    [Fact]
    public void InvalidOrOldSettingsHaveBoundedDefaults()
    {
        Assert.Equal(new TransferPolicy(), TransferPolicy.Parse("broken"));
        Assert.Equal(new TransferPolicy(), TransferPolicy.Parse("{}"));
        var policy = TransferPolicy.Parse("""
        {
            "mode":"unknown",
            "maxConcurrency":100,
            "manualConcurrency":0,
            "checkers":999,
            "maxBacklog":10,
            "orderBy":"invalid_order",
            "bufferSizeKib":8,
            "memoryBudgetMb":1,
            "multiThreadCutoffMib":0,
            "multiThreadStreams":99,
            "checkMode":"bad_mode",
            "inplace":true,
            "preallocate":false,
            "flushOnClose":false,
            "bandwidthMib":-1,
            "lowLevelRetries":0,
            "highLevelRetries":999,
            "retrySleepMs":1
        }
        """);
        Assert.Equal("automatic", policy.Mode);
        Assert.Equal(32, policy.MaxConcurrency);
        Assert.Equal(1, policy.ManualConcurrency);
        Assert.Equal(64, policy.Checkers);
        Assert.Equal(128, policy.MaxBacklog);
        Assert.Equal("default", policy.OrderBy);
        Assert.Equal(64, policy.BufferSizeKib);
        Assert.Equal(16, policy.MemoryBudgetMb);
        Assert.Equal(1, policy.MultiThreadCutoffMib);
        Assert.Equal(16, policy.MultiThreadStreams);
        Assert.Equal("size_and_mod_time", policy.CheckMode);
        Assert.True(policy.Inplace);
        Assert.False(policy.Preallocate);
        Assert.False(policy.FlushOnClose);
        Assert.Equal(0, policy.BandwidthMib);
        Assert.Equal(1, policy.LowLevelRetries);
        Assert.Equal(20, policy.HighLevelRetries);
        Assert.Equal(10, policy.RetrySleepMs);
    }

    [Fact]
    public async Task DefaultsAndTaskOverridesPersistIndependentlyAndCanBeCleared()
    {
        var ipc = new ConfigurableIpc();
        var backend = new FileOperationService(ipc);
        var policy = new TransferPolicy
        {
            Mode = "manual",
            ManualConcurrency = 7,
            Checkers = 16,
            MaxBacklog = 4096,
            OrderBy = "size_asc",
            BufferSizeKib = 8192,
            MultiThreadCutoffMib = 32,
            MultiThreadStreams = 6,
            CheckMode = "checksum",
            Inplace = true,
            Preallocate = false,
            FlushOnClose = false,
            BandwidthMib = 12,
            LowLevelRetries = 15,
            HighLevelRetries = 5,
            RetrySleepMs = 250,
        };
        await TransferPolicyStore.SaveAsync(backend, policy);
        await TransferPolicyStore.SetOverrideAsync(backend, "job-a", policy with { ManualConcurrency = 2 });
        await TransferPolicyStore.SetOverrideAsync(backend, "job-b", policy with { ManualConcurrency = 3 });
        Assert.Equal(policy, await TransferPolicyStore.LoadAsync(backend));
        Assert.Contains("job-a", ipc.Settings["transfer.overrides.v1"]);
        Assert.Contains("job-b", ipc.Settings["transfer.overrides.v1"]);
        await TransferPolicyStore.SetOverrideAsync(backend, "job-a", null);
        Assert.DoesNotContain("job-a", ipc.Settings["transfer.overrides.v1"]);
        Assert.Contains("job-b", ipc.Settings["transfer.overrides.v1"]);
        await TransferPolicyStore.ClearLearningAsync(backend);
        Assert.Equal("{}", ipc.Settings["transfer.learning.v1"]);
    }

    [Fact]
    public void RetryStatusClearsWithoutChangingCustomLimits()
    {
        var operation = new TransferOperationViewModel(false, [@"C:\source"], @"D:\destination", (_, _) => Task.FromResult(TransferOperationStatus.Completed));
        operation.SetOperationId("storage-job");
        operation.ApplyTuning(new TuningUpdate { OperationId = "storage-job", Mode = "manual", Concurrency = 16,
            Explanation = "Retrying failed I/O | Read source timed out", ActiveFiles = [new() { Path = "image.jpg", Total = 10 }] });
        Assert.Contains("Retrying failed I/O", operation.TuningText);
        Assert.Single(operation.ActiveFiles);
        operation.ApplyTuning(new TuningUpdate { OperationId = "storage-job", Mode = "manual", Concurrency = 16,
            Explanation = "Custom limits (memory and provider limits still apply)", ActiveFiles = [new() { Path = "image.jpg", Current = 8, Total = 10 }] });
        Assert.Contains("up to 16 files", operation.TuningText);
        Assert.DoesNotContain("safety limit", operation.TuningText);
        Assert.DoesNotContain("Retrying failed I/O", operation.TuningText);
        operation.Complete(TransferOperationStatus.Completed);
        Assert.Empty(operation.TuningText);
        Assert.Empty(operation.ActiveFiles);
    }

    [Fact]
    public void ActiveFilesDisappearOnCompletionAndLateOrUnrelatedEventsAreIgnored()
    {
        var operation = new TransferOperationViewModel(false, [@"C:\source"], @"D:\destination", (_, _) => Task.FromResult(TransferOperationStatus.Completed));
        operation.SetOperationId("job-a");
        operation.ApplyTuning(new TuningUpdate { OperationId = "job-b", ActiveFiles = [new() { Path = "wrong" }] });
        Assert.Empty(operation.ActiveFiles);
        operation.ApplyTuning(new TuningUpdate { OperationId = "job-a", Concurrency = 2,
            ActiveFiles = [new() { Path = "first", Total = 10 }, new() { Path = "second", Total = 20 }] });
        Assert.Equal(2, operation.ActiveFiles.Count);
        var retainedRow = operation.ActiveFiles[1];
        operation.ApplyTuning(new TuningUpdate { OperationId = "job-a", Concurrency = 2,
            ActiveFiles = [new() { Path = "second", Current = 10, Total = 20 }] });
        Assert.Same(retainedRow, Assert.Single(operation.ActiveFiles));
        operation.Complete(TransferOperationStatus.Completed);
        operation.ApplyTuning(new TuningUpdate { OperationId = "job-a", ActiveFiles = [new() { Path = "late" }] });
        Assert.Empty(operation.ActiveFiles);
        Assert.Empty(operation.TuningText);
    }
}
