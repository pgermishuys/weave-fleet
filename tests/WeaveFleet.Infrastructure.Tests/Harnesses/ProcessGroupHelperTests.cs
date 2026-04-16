using System.Diagnostics;
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
    public void KillProcessGroup_NonExistentPid_DoesNotThrow()
    {
        // Use a very large PID that is extremely unlikely to exist
        var ex = Record.Exception(() => ProcessGroupHelper.KillProcessGroup(int.MaxValue));
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
            var ex = Record.Exception(() => ProcessGroupHelper.KillProcessGroup(process.Id));
            ex.ShouldBeNull();
        }
        finally
        {
            SafeKill(process);
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
