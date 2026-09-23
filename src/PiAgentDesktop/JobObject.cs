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
    private const int JobObjectBasicProcessIdListClass = 3;
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

    /// <summary>
    /// Process ids currently assigned to the job, i.e. every pi-web process tree this app
    /// has spawned - including any that a failed or timed-out start leaked and that the
    /// server no longer tracks.
    /// </summary>
    public IReadOnlyList<int> GetProcessIds()
    {
        if (_handle == IntPtr.Zero)
        {
            return Array.Empty<int>();
        }

        // Ask for the required buffer size first: a deliberately small buffer fails with
        // ERROR_MORE_DATA and reports how much is actually needed, so the real read can
        // never come up short and silently report "no processes".
        var probeSize = 8 + IntPtr.Size;
        var probe = Marshal.AllocHGlobal(probeSize);
        uint needed;

        try
        {
            QueryInformationJobObject(
                _handle, JobObjectBasicProcessIdListClass, probe, (uint)probeSize, out needed);
        }
        finally
        {
            Marshal.FreeHGlobal(probe);
        }

        if (needed < 8 || needed > 1_048_576)
        {
            return Array.Empty<int>();
        }

        var buffer = Marshal.AllocHGlobal((int)needed);

        try
        {
            if (!QueryInformationJobObject(
                    _handle, JobObjectBasicProcessIdListClass, buffer, needed, out _))
            {
                return Array.Empty<int>();
            }

            var count = Marshal.ReadInt32(buffer, 4);
            var ids = new List<int>();
            var capacity = (int)((needed - 8) / (uint)IntPtr.Size);

            for (var i = 0; i < count && i < capacity; i++)
            {
                var pid = Marshal.ReadIntPtr(buffer, 8 + (IntPtr.Size * i)).ToInt64();
                if (pid > 0)
                {
                    ids.Add((int)pid);
                }
            }

            return ids;
        }
        catch
        {
            return Array.Empty<int>();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Terminates every process still assigned to the job and returns how many. This is
    /// how leftover servers get reaped: each of them holds the pi-web package directory,
    /// so npm cannot replace it (EBUSY) until all of them are gone.
    /// </summary>
    public int KillAll(Log log)
    {
        var killed = 0;

        foreach (var pid in GetProcessIds())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                log.Warn($"Reaping leftover pi-web process (pid {pid}, {process.ProcessName}).");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
                killed++;
            }
            catch (ArgumentException)
            {
                /* already gone */
            }
            catch (Exception error)
            {
                log.Warn($"Could not terminate pid {pid}: {error.Message}");
            }
        }

        return killed;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint informationLength, out uint returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
