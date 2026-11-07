using System.Diagnostics;
using SimpleFile.Core;

namespace SimpleFile.PluginHost.Backend;

public interface IPluginProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    event EventHandler? Exited;
    void Kill();
}

public interface IPluginProcessLauncher
{
    IPluginProcess Launch(
        string executable,
        IReadOnlyList<string> args,
        string workingDirectory,
        Action<string> onStderrLine);
}

public sealed class ProcessLauncher : IPluginProcessLauncher
{
    public IPluginProcess Launch(
        string executable,
        IReadOnlyList<string> args,
        string workingDirectory,
        Action<string> onStderrLine)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        // Created first: a backend that cannot be tied to a job must not start.
        var job = JobObject.Create();
        Process? process = null;
        try
        {
            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { } line)
                {
                    try { onStderrLine(line); } catch { }
                }
            };
            process.Start();
            try
            {
                job.Assign(process);
            }
            catch
            {
                try { process.Kill(true); } catch { }
                throw;
            }

            process.BeginErrorReadLine();
            return new SystemPluginProcess(process, job);
        }
        catch
        {
            process?.Dispose();
            job.Dispose();
            throw;
        }
    }

    private sealed class SystemPluginProcess : IPluginProcess
    {
        private readonly Process _process;
        private readonly JobObject _job;
        private int _disposed;

        public SystemPluginProcess(Process process, JobObject job)
        {
            _process = process;
            _job = job;
            Id = process.Id;
            process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
        }

        public int Id { get; }

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch { return true; }
            }
        }

        public event EventHandler? Exited;

        public void Kill()
        {
            try { _process.Kill(true); } catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            Kill();
            _job.Dispose();
            _process.Dispose();
        }
    }
}
