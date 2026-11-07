using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using SimpleFile.PluginHost.Backend;
using Xunit;

namespace SimpleFile.Tests;

public class PluginProcessLauncherTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task Launch_ShortLivedProcess_RaisesExited()
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new ProcessLauncher().Launch(Cmd, ["/c", "exit", "3"], Environment.SystemDirectory, _ => { });
        process.Exited += (_, _) => exited.TrySetResult();
        if (process.HasExited)
        {
            exited.TrySetResult();
        }

        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task Launch_ForwardsStderrLines()
    {
        var lines = new ConcurrentQueue<string>();
        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new ProcessLauncher().Launch(
            Cmd,
            ["/c", "echo err 1>&2"],
            Environment.SystemDirectory,
            line =>
            {
                lines.Enqueue(line);
                got.TrySetResult();
            });

        await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(lines, l => l.Contains("err"));
    }

    [Fact]
    public async Task Dispose_EndsLongRunningProcess()
    {
        var process = new ProcessLauncher().Launch(
            Cmd, ["/c", "ping 127.0.0.1 -n 30 >nul"], Environment.SystemDirectory, _ => { });
        Assert.False(process.HasExited);

        process.Dispose();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        // HasExited is unusable after Dispose, so observe via the process id instead.
        while (DateTime.UtcNow < deadline && IsRunning(process.Id))
        {
            await Task.Delay(50);
        }

        Assert.False(IsRunning(process.Id));
    }

    private static bool IsRunning(int id)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(id);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
