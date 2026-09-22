using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="ProcessGroupHelper"/>.
/// Platform-specific tests are skipped via <see cref="FactAttribute"/> with runtime checks.
/// </summary>
public sealed class ProcessGroupHelperTests
{
    [Fact]
    public void AssignToProcessGroup_NullProcess_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            ProcessGroupHelper.AssignToProcessGroup(null!));
        ex.ParamName.ShouldBe("process");
    }

    [Fact]
    public void KillProcessGroup_NullProcess_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            ProcessGroupHelper.KillProcessGroup(null!));
        ex.ParamName.ShouldBe("process");
    }

    [Fact]
    public void KillProcessGroup_ProcessThatWasNeverStarted_DoesNotThrow()
    {
        using var process = new Process();
        var ex = Record.Exception(() => ProcessGroupHelper.KillProcessGroup(process));
        ex.ShouldBeNull();
    }

    [Fact]
    public async Task KillProcessGroup_ExitedProcess_DoesNotThrow()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return; // Skip on non-Unix

        using var process = Process.Start(new ProcessStartInfo("true") { UseShellExecute = false })!;
        await process.WaitForExitAsync();

        var ex = Record.Exception(() => ProcessGroupHelper.KillProcessGroup(process));
        ex.ShouldBeNull();
    }

    [Fact]
    public void AssignToProcessGroup_UnixProcess_AssignsWithoutException()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return; // Skip on non-Unix

        using var process = SpawnSleepProcess();
        try
        {
            var handle = ProcessGroupHelper.AssignToProcessGroup(process);
            handle.ShouldBeNull(); // Unix returns null
        }
        finally
        {
            SafeKill(process);
        }
    }

    [Fact]
    public void KillProcessGroup_UnixProcess_DoesNotThrow()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return; // Skip on non-Unix

        using var process = SpawnSleepProcess();
        try
        {
            ProcessGroupHelper.AssignToProcessGroup(process);

            // KillProcessGroup is best-effort; it should not throw even if the
            // process group kill fails (e.g. due to setpgid race on exec).
            var ex = Record.Exception(() => ProcessGroupHelper.KillProcessGroup(process));
            ex.ShouldBeNull();
        }
        finally
        {
            SafeKill(process);
        }
    }

    [Fact]
    public async Task KillProcessGroup_UnixProcess_KillsItAndItsChildrenStraightAway()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return; // Skip on non-Unix

        // A harness is a process with children (a CLI and the tools it runs). setpgid fails once the child has
        // exec'd, so there's often no group to kill: the kill must still reach the process and its children.
        var psi = new ProcessStartInfo
        {
            FileName = "sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("sleep 30 & echo $!; wait");

        using var process = new Process { StartInfo = psi };
        process.Start();
        var childPid = int.Parse((await process.StandardOutput.ReadLineAsync())!, CultureInfo.InvariantCulture);
        try
        {
            ProcessGroupHelper.AssignToProcessGroup(process);

            ProcessGroupHelper.KillProcessGroup(process);

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilGoneAsync(childPid, TimeSpan.FromSeconds(2));
        }
        finally
        {
            SafeKill(process);
            SafeKill(childPid);
        }
    }

    [Fact]
    public void AssignToProcessGroup_WindowsProcess_ReturnsJobObjectHandle()
    {
        if (!OperatingSystem.IsWindows())
            return; // Skip on non-Windows

        using var process = SpawnWindowsSleepProcess();
        try
        {
            var handle = ProcessGroupHelper.AssignToProcessGroup(process);
            handle.ShouldNotBeNull();
            handle!.IsInvalid.ShouldBeFalse();
            handle.Dispose();
        }
        finally
        {
            SafeKill(process);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static Process SpawnSleepProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sleep",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("30");

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
    }

    private static Process SpawnWindowsSleepProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "timeout",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/t");
        psi.ArgumentList.Add("30");
        psi.ArgumentList.Add("/nobreak");

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
    }

    private static async Task WaitUntilGoneAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (IsRunning(pid))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Process {pid} is still running.");
            await Task.Delay(50);
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void SafeKill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            SafeKill(process);
        }
        catch (ArgumentException)
        {
            // Already gone
        }
    }

    private static void SafeKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort
        }
    }
}
