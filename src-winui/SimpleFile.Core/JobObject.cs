using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SimpleFile.Core;

/// <summary>
/// Owns <c>simplefile-service</c> so the backend dies with the UI, like Explorer
/// owning its shell extensions — without dragging opened documents with it.
/// <c>KILL_ON_JOB_CLOSE</c> still kills the service when the last job handle
/// closes. <c>SILENT_BREAKAWAY_OK</c> keeps ShellExecute/Open With children
/// (Notepad, viewers, players) out of the job so they survive SimpleFile exit.
/// </summary>
internal sealed class JobObject : IDisposable
{
    internal const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
    internal const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x00001000;
    internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    internal const uint DefaultLimitFlags =
        JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;

    private IntPtr _handle;
    private bool _disposed;

    private JobObject(IntPtr handle)
    {
        _handle = handle;
    }

    public static JobObject? TryCreate()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = DefaultLimitFlags,
            },
        };

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                CloseHandle(handle);
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new JobObject(handle);
    }

    internal uint? TryGetLimitFlags()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(
                    _handle,
                    JobObjectExtendedLimitInformation,
                    buffer,
                    (uint)size,
                    out _))
            {
                return null;
            }

            var info = Marshal.PtrToStructure<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(buffer);
            return info.BasicLimitInformation.LimitFlags;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public bool TryAssign(Process process)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return AssignProcessToJobObject(_handle, process.Handle);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int infoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        IntPtr hJob,
        int infoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength,
        out uint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

#pragma warning disable CS0649 // Fields are populated for native interop.
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
#pragma warning restore CS0649
}
