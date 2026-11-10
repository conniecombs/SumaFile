using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SimpleFile.PluginHost.Backend;

/// <summary>Which process serves a connected named pipe.</summary>
internal static class PipeServerIdentity
{
    /// <summary>
    /// The id of the process serving <paramref name="pipe"/>, from <c>GetNamedPipeServerProcessId</c>; null where that
    /// call does not exist (not Windows), so the caller skips its check there. Throws <see cref="Win32Exception"/> when
    /// Windows cannot tell, so a caller that requires an answer fails closed.
    /// </summary>
    public static int? ServerProcessId(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return checked((int)processId);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
