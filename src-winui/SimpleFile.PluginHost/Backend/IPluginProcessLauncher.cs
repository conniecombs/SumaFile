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
    /// <summary>
    /// Starts a backend. Each <paramref name="environment"/> entry is set on top of the environment SumaFile's own
    /// process passes on (the host sets <c>TMP</c> and <c>TEMP</c> to the plugin's data folder).
    /// </summary>
    IPluginProcess Launch(
        string executable,
        IReadOnlyList<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        Action<string> onStderrLine);

    /// <summary>
    /// Starts a backend and gives it <paramref name="launchToken"/>: the variable
    /// <see cref="ProcessLauncher.AuthStdinVariable"/> is set to <c>1</c> in its environment and the token is the
    /// first line of its standard input. A backend that reads it answers only a <c>plugin.handshake</c> that carries
    /// the token. With no token, or in a launcher that cannot pass one, the backend starts as it always did and
    /// answers every handshake.
    /// </summary>
    IPluginProcess Launch(
        string executable,
        IReadOnlyList<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        Action<string> onStderrLine,
        string? launchToken) =>
        Launch(executable, args, workingDirectory, environment, onStderrLine);
}

public sealed class ProcessLauncher : IPluginProcessLauncher
{
    /// <summary>Set to <c>1</c> in a backend's environment when its launch token is on its standard input.</summary>
    public const string AuthStdinVariable = "SUMAFILE_PLUGIN_AUTH_STDIN";

    public IPluginProcess Launch(
        string executable,
        IReadOnlyList<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        Action<string> onStderrLine) =>
        Launch(executable, args, workingDirectory, environment, onStderrLine, launchToken: null);

    public IPluginProcess Launch(
        string executable,
        IReadOnlyList<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        Action<string> onStderrLine,
        string? launchToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        if (launchToken is not null)
        {
            start.RedirectStandardInput = true;
            // No byte-order mark: the backend reads the line as the token itself.
            start.StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        // ProcessStartInfo.Environment starts as a copy of this process's environment, so only these entries change.
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        // Never inherited from SumaFile's own environment: it says what is on this backend's standard input.
        if (launchToken is not null)
        {
            start.Environment[AuthStdinVariable] = "1";
        }
        else
        {
            start.Environment.Remove(AuthStdinVariable);
        }

        // Created first: a backend that cannot be tied to a job must not start.
        var job = CreateJob();
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
            if (launchToken is not null)
            {
                try
                {
                    process.StandardInput.WriteLine(launchToken);
                    process.StandardInput.Close();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // The backend is already gone; the connect that follows reports it.
                }
            }

            return new SystemPluginProcess(process, job);
        }
        catch
        {
            process?.Dispose();
            job.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Kill-on-close only. Unlike the service's job, no breakaway: a backend's children stay in the job and
    /// die with it, even when SumaFile crashes and never runs <see cref="IPluginProcess.Dispose"/>.
    /// </summary>
    internal static JobObject CreateJob() => JobObject.CreateKillOnCloseOnly();

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
