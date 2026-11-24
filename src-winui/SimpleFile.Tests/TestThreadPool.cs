using System.Runtime.CompilerServices;

namespace SimpleFile.Tests;

/// <summary>
/// Gives the test run a thread pool that does not have to grow first.
///
/// xUnit runs the test classes side by side, and many tests hold a pool thread for a moment (a fake that
/// blocks, a wait on a task). With every thread busy, the pool adds new ones slowly, one or two a second, and
/// work queued with <c>Task.Run</c> waits that long to start. Tests that give such work a time budget then
/// fail on a loaded machine although nothing is wrong:
/// <c>InvalidateDecorations_SlowSourceThatAnswersNothing_ClearsItsStaleDecorations</c> saw its instant source
/// miss a two-second budget on the CI runner. Threads up to this floor are started as soon as they are needed.
/// </summary>
internal static class TestThreadPool
{
    private const int MinimumThreads = 64;

    [ModuleInitializer]
    internal static void RaiseTheFloor()
    {
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, MinimumThreads), Math.Max(completionPorts, MinimumThreads));
    }
}
