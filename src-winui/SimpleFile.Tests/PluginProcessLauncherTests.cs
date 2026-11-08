using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SimpleFile.Core;
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

    [Fact]
    public void BackendJob_IsKillOnCloseWithoutBreakaway()
    {
        using var job = ProcessLauncher.CreateJob();

        Assert.Equal(JobObject.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, job.TryGetLimitFlags());
    }

    [Fact]
    public async Task BackendJob_Close_KillsTheBackendsChildProcesses()
    {
        // The first ping gives Assign time to finish, so the second one is created inside the job. Closing the
        // job alone (no Kill(true), as when SumaFile crashes) must take the child down with it.
        var job = ProcessLauncher.CreateJob();
        using var parent = Process.Start(new ProcessStartInfo(Cmd, "/c \"ping 127.0.0.1 -n 2 >nul & ping 127.0.0.1 -n 30 >nul\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        int[] children = [];
        try
        {
            job.Assign(parent);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                // The second ping: started after the first one exited, at least ~1 s in.
                children = ChildProcessIds(parent.Id, "PING.EXE");
                if (children.Length > 0 && DateTime.UtcNow - parent.StartTime.ToUniversalTime() > TimeSpan.FromSeconds(1.5)) break;
                await Task.Delay(100);
            }

            Assert.NotEmpty(children);

            job.Dispose();

            var gone = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < gone && children.Any(IsRunning))
            {
                await Task.Delay(50);
            }

            Assert.DoesNotContain(children, IsRunning);
        }
        finally
        {
            job.Dispose();
            foreach (var id in children)
            {
                try { using var p = Process.GetProcessById(id); p.Kill(); } catch { }
            }

            try { parent.Kill(true); } catch { }
        }
    }

    private static int[] ChildProcessIds(int parentId, string exeName)
    {
        var result = new List<int>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == new IntPtr(-1)) return [];
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
            {
                if (entry.th32ParentProcessID == parentId
                    && string.Equals(entry.szExeFile, exeName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add((int)entry.th32ProcessID);
                }
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result.ToArray();
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

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
