using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// Cross-platform utility for assigning child processes to a process group (Unix)
/// or a Job Object (Windows) so they are killed when the parent exits.
/// </summary>
internal static class ProcessGroupHelper
{
    // LoggerMessage delegates — required by CA1848
    private static readonly Action<ILogger, int, Exception?> LogAssigned =
        LoggerMessage.Define<int>(LogLevel.Debug, new EventId(1, "ProcessGroupAssigned"),
            "Assigned process {Pid} to its own process group");

    private static readonly Action<ILogger, int, int, int, Exception?> LogSetpgidFailed =
        LoggerMessage.Define<int, int, int>(LogLevel.Warning, new EventId(2, "SetpgidFailed"),
            "setpgid({Pid}, {Pgid}) failed with errno {Errno}");

    private static readonly Action<ILogger, int, int, Exception?> LogKillpgFailed =
        LoggerMessage.Define<int, int>(LogLevel.Warning, new EventId(3, "KillpgFailed"),
            "killpg({Pgid}, SIGKILL) failed with errno {Errno}");

    private static readonly Action<ILogger, int, Exception?> LogKillpgSent =
        LoggerMessage.Define<int>(LogLevel.Debug, new EventId(4, "KillpgSent"),
            "Sent SIGKILL to process group {Pgid}");

    private static readonly Action<ILogger, int, Exception?> LogAssignFailed =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(5, "AssignFailed"),
            "Failed to assign process {Pid} to process group");

    private static readonly Action<ILogger, int, Exception?> LogKillFailed =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(6, "KillFailed"),
            "Failed to kill process group {Pgid}");

    private static readonly Action<ILogger, int, Exception?> LogJobObjectAssigned =
        LoggerMessage.Define<int>(LogLevel.Debug, new EventId(7, "JobObjectAssigned"),
            "Assigned process {Pid} to Job Object");

    private static readonly Action<ILogger, int, int, Exception?> LogJobObjectAssignFailed =
        LoggerMessage.Define<int, int>(LogLevel.Warning, new EventId(8, "JobObjectAssignFailed"),
            "AssignProcessToJobObject failed for process {Pid} with error {Error}");

    private static readonly Action<ILogger, int, Exception?> LogCreateJobFailed =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(9, "CreateJobFailed"),
            "CreateJobObject failed with error {Error}");

    private static readonly Action<ILogger, int, Exception?> LogSetInfoFailed =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(10, "SetInfoFailed"),
            "SetInformationJobObject failed with error {Error}");

    private static readonly Action<ILogger, int, Exception?> LogKillTreeFailed =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(11, "KillTreeFailed"),
            "Failed to kill process {Pid}");

    private static readonly Action<ILogger, int, Exception?> LogKillTreeDone =
        LoggerMessage.Define<int>(LogLevel.Debug, new EventId(12, "KillTreeDone"),
            "Killed process tree for pid {Pid}");

    /// <summary>
    /// Assigns <paramref name="process"/> to its own process group immediately after start.
    /// On Unix: calls <c>setpgid(pid, pid)</c> to make the child its own process group leader.
    /// On Windows: creates a Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> and
    /// assigns the process to it. Returns the Job Object handle (must be kept alive by the caller).
    /// </summary>
    /// <returns>
    /// On Windows: a <see cref="SafeHandle"/> for the Job Object that must be kept alive.
    /// On Unix: <c>null</c>.
    /// </returns>
    internal static SafeHandle? AssignToProcessGroup(Process process, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (OperatingSystem.IsWindows())
        {
            return AssignToJobObject(process, logger);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            AssignToUnixProcessGroup(process, logger);
        }

        return null;
    }

    /// <summary>
    /// Kills the process group associated with <paramref name="pid"/> on Unix,
    /// or kills the process tree on Windows.
    /// </summary>
    internal static void KillProcessGroup(int pid, ILogger? logger = null)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            KillUnixProcessGroup(pid, logger);
        }
        else if (OperatingSystem.IsWindows())
        {
            KillWindowsProcess(pid, logger);
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static void AssignToUnixProcessGroup(Process process, ILogger? logger)
    {
        try
        {
            int result = Unix.setpgid(process.Id, process.Id);
            if (result != 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (logger is not null)
                    LogSetpgidFailed(logger, process.Id, process.Id, errno, null);
            }
            else
            {
                if (logger is not null)
                    LogAssigned(logger, process.Id, null);
            }
        }
        catch (Exception ex)
        {
            if (logger is not null)
                LogAssignFailed(logger, process.Id, ex);
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static void KillUnixProcessGroup(int pid, ILogger? logger)
    {
        try
        {
            // SIGKILL = 9 — unconditional kill; SIGTERM (15) may be ignored by some processes
            int result = Unix.killpg(pid, 9);
            if (result != 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (logger is not null)
                    LogKillpgFailed(logger, pid, errno, null);
            }
            else
            {
                if (logger is not null)
                    LogKillpgSent(logger, pid, null);
            }
        }
        catch (Exception ex)
        {
            if (logger is not null)
                LogKillFailed(logger, pid, ex);
        }
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle AssignToJobObject(Process process, ILogger? logger)
    {
        var jobHandle = Windows.CreateJobObject(IntPtr.Zero, null);
        if (jobHandle.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            if (logger is not null)
                LogCreateJobFailed(logger, err, null);
            return jobHandle;
        }

        var limitInfo = new Windows.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new Windows.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = Windows.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        int size = Marshal.SizeOf<Windows.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr infoPtr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limitInfo, infoPtr, false);
            bool ok = Windows.SetInformationJobObject(
                jobHandle,
                Windows.JobObjectInfoType.ExtendedLimitInformation,
                infoPtr,
                (uint)size);

            if (!ok)
            {
                int err = Marshal.GetLastPInvokeError();
                if (logger is not null)
                    LogSetInfoFailed(logger, err, null);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
        }

        bool assigned = Windows.AssignProcessToJobObject(jobHandle, process.SafeHandle);
        if (!assigned)
        {
            int err = Marshal.GetLastPInvokeError();
            if (logger is not null)
                LogJobObjectAssignFailed(logger, process.Id, err, null);
            _ = err; // suppress unused variable if logger is null
        }
        else
        {
            if (logger is not null)
                LogJobObjectAssigned(logger, process.Id, null);
        }

        return jobHandle;
    }

    [SupportedOSPlatform("windows")]
    private static void KillWindowsProcess(int pid, ILogger? logger)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            if (logger is not null)
                LogKillTreeDone(logger, pid, null);
        }
        catch (ArgumentException)
        {
            // Process already exited — not an error
        }
        catch (Exception ex)
        {
            if (logger is not null)
                LogKillTreeFailed(logger, pid, ex);
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static class Unix
    {
        [DllImport("libc", SetLastError = true)]
        internal static extern int setpgid(int pid, int pgid);

        [DllImport("libc", SetLastError = true)]
        internal static extern int killpg(int pgrp, int sig);
    }

    [SupportedOSPlatform("windows")]
    private static class Windows
    {
        internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        internal enum JobObjectInfoType
        {
            ExtendedLimitInformation = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        internal struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern SafeFileHandle CreateJobObject(
            IntPtr lpJobAttributes,
            string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeHandle hJob,
            JobObjectInfoType infoType,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(
            SafeHandle hJob,
            SafeHandle hProcess);
    }
}
