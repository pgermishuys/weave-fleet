using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// Makes sure the harness processes Fleet starts don't outlive it.
/// <para>
/// On Windows each one goes into a Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: when Fleet exits,
/// however it exits, the system closes the job and kills the process with it.
/// </para>
/// <para>
/// On Linux and macOS nothing kills a child when its parent dies, and .NET can't run code between fork and exec to set
/// that up (a process group of its own, or a parent-death signal): by the time <c>Process.Start</c> returns the child
/// has exec'd, so <c>setpgid</c> fails with <c>EACCES</c> and the child stays in Fleet's process group. So Fleet keeps
/// track of them instead. <see cref="KillProcessGroup"/> kills a harness and its children when Fleet stops it; any
/// still running when Fleet's process exits (normally, or on an unhandled exception) are killed then
/// (<see cref="KillRunning"/>); and each is recorded on disk (<see cref="HarnessProcessRecords"/>), so the ones a
/// Fleet that was killed outright (<c>SIGKILL</c>, the out-of-memory killer, a crash in native code) left running are
/// stopped by the next Fleet to start.
/// </para>
/// </summary>
internal static class ProcessGroupHelper
{
    // LoggerMessage delegates — required by CA1848
    private static readonly Action<ILogger, int, string, Exception?> LogTracked =
        LoggerMessage.Define<int, string>(LogLevel.Debug, new EventId(1, "HarnessProcessTracked"),
            "Tracking harness process {Pid} ({Name}) until it exits");

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

    // The harness processes this Fleet started on Linux or macOS that haven't exited, by pid.
    private static readonly ConcurrentDictionary<int, ProcessIdentity> Running = new();
    private static HarnessProcessRecords? _records;
    private static int _exitHooked;

    /// <summary>
    /// Records each harness process started from now on in <paramref name="records"/> (Linux and macOS), so the next
    /// Fleet can stop it if this one dies without stopping it. <see langword="null"/> stops recording.
    /// </summary>
    internal static void UseRecords(HarnessProcessRecords? records) => Volatile.Write(ref _records, records);

    /// <summary>The harness processes started and not yet exited (Linux and macOS).</summary>
    internal static IReadOnlyCollection<ProcessIdentity> RunningProcesses => [.. Running.Values];

    /// <summary>
    /// Takes charge of <paramref name="process"/>, just started, so it doesn't outlive Fleet.
    /// On Windows: creates a Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> and assigns the process to it.
    /// On Linux and macOS: tracks it, and records it on disk, until it exits (see the class summary).
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

        if (ProcessIdentity.IsSupported)
        {
            Track(process, logger);
        }

        return null;
    }

    /// <summary>Kills <paramref name="process"/> and its children, straight away.</summary>
    internal static void KillProcessGroup(Process process, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(process);

        // An exited harness's pid may already belong to another process.
        if (HasExited(process))
            return;

        KillProcessTree(process, logger);
    }

    /// <summary>
    /// Kills every harness process this Fleet started that's still running, with its children, and removes their
    /// records. Runs when Fleet's process exits: a normal shutdown has stopped them all by then, so it's for any the
    /// shutdown didn't reach. <paramref name="only"/> limits it to those pids (for tests).
    /// </summary>
    internal static void KillRunning(IReadOnlyCollection<int>? only = null)
    {
        var records = Volatile.Read(ref _records);
        foreach (var (pid, identity) in Running)
        {
            if (only is not null && !only.Contains(pid))
                continue;

            try
            {
                // Only the process Fleet started: the pid may have gone to another since.
                if (identity.IsSameProcess(ProcessIdentity.Read(pid)))
                {
                    using var process = Process.GetProcessById(pid);
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception
                                           or AggregateException or NotSupportedException)
            {
                // Gone already, or can't be killed: nothing more to do on the way out.
            }

            Running.TryRemove(new KeyValuePair<int, ProcessIdentity>(pid, identity));
            records?.Remove(identity);
        }
    }

    private static void Track(Process process, ILogger? logger)
    {
        int pid;
        try
        {
            pid = process.Id;
        }
        catch (InvalidOperationException)
        {
            return; // Never started.
        }

        if (ProcessIdentity.Read(pid) is not { } identity)
            return; // Exited already.

        HookProcessExit();
        var records = Volatile.Read(ref _records);
        Running[pid] = identity;
        records?.Add(identity, process.StartInfo.FileName);
        if (logger is not null)
            LogTracked(logger, pid, identity.Name, null);

        void Forget()
        {
            if (Running.TryRemove(new KeyValuePair<int, ProcessIdentity>(pid, identity)))
                records?.Remove(identity);
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Forget();
        if (HasExited(process))
            Forget();
    }

    private static void HookProcessExit()
    {
        if (Interlocked.Exchange(ref _exitHooked, 1) == 1)
            return;

        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillRunning();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => KillRunning();
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

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // No OS process was ever associated with it.
            return true;
        }
    }

    // Through the Process object, not the pid: a harness that just exited may have had its pid reused.
    private static void KillProcessTree(Process process, ILogger? logger)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            if (logger is not null)
                LogKillTreeDone(logger, process.Id, null);
        }
        catch (InvalidOperationException)
        {
            // Process already exited — not an error
        }
        catch (Exception ex)
        {
            if (logger is not null)
                LogKillTreeFailed(logger, process.Id, ex);
        }
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
