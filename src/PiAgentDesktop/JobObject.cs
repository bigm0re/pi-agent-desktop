using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PiAgentDesktop;

/// <summary>
/// Win32 job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. The node server is
/// assigned to it so the whole process tree dies with the desktop app even when
/// the app is force-killed or crashes.
/// </summary>
internal sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private IntPtr _handle;

    private JobObject(IntPtr handle) => _handle = handle;

    public static JobObject? Create(Log log)
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            log.Warn($"CreateJobObject failed ({Marshal.GetLastWin32Error()}); falling back to process-tree kill only.");
            return null;
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
            {
                log.Warn($"SetInformationJobObject failed ({Marshal.GetLastWin32Error()}).");
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

    public void Attach(Process process, Log log)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!AssignProcessToJobObject(_handle, process.Handle))
            {
                log.Warn($"AssignProcessToJobObject failed ({Marshal.GetLastWin32Error()}); process-tree kill will still be used.");
            }
        }
        catch (Exception error)
        {
            log.Warn($"AssignProcessToJobObject threw: {error.Message}");
        }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
