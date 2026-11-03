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
        var policy = TransferPolicy.Parse("""{"mode":"unknown","maxConcurrency":100,"manualConcurrency":0,"memoryBudgetMb":1,"bandwidthMib":-1}""");
        Assert.Equal("automatic", policy.Mode);
        Assert.Equal(32, policy.MaxConcurrency);
        Assert.Equal(1, policy.ManualConcurrency);
        Assert.Equal(16, policy.MemoryBudgetMb);
        Assert.Equal(0, policy.BandwidthMib);
    }

    [Fact]
    public async Task DefaultsAndTaskOverridesPersistIndependentlyAndCanBeCleared()
    {
        var ipc = new ConfigurableIpc();
        var backend = new FileOperationService(ipc);
        var policy = new TransferPolicy { Mode = "manual", ManualConcurrency = 7, BandwidthMib = 12 };
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
