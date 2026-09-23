using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.IntegrationTests.Harnesses;

/// <summary>
/// A Fleet killed outright (<c>SIGKILL</c>, a crash) can't stop its harness processes, and on Linux and macOS nothing
/// else does. The next Fleet to start stops the ones its records prove a Fleet that's gone started, and nothing else.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HarnessProcessCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-process-cleanup-{Guid.NewGuid():N}");
    private readonly List<Process> _started = [];

    public void Dispose()
    {
        foreach (var process in _started)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
        }

        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public async Task Starting_Fleet_stops_what_a_Fleet_that_was_killed_left_running_and_nothing_else()
    {
        if (!ProcessIdentity.IsSupported)
            return;

        var records = Path.Combine(_root, "harness-processes");

        // The Fleet that was killed: it exited, and its harness (and that harness's own child) kept running.
        var killedFleet = Sleep();
        var killedFleetIdentity = ProcessIdentity.Read(killedFleet.Id).ShouldNotBeNull();
        killedFleet.Kill();
        await killedFleet.WaitForExitAsync();
        var leftover = Start("sh", redirect: true, "-c", "sleep 60 & echo $!; wait");
        var leftoverChild = int.Parse((await leftover.StandardOutput.ReadLineAsync())!, System.Globalization.CultureInfo.InvariantCulture);
        _started.Add(Process.GetProcessById(leftoverChild));
        new HarnessProcessRecords(records, NullLogger.Instance) { Owner = killedFleetIdentity }
            .Add(ProcessIdentity.Read(leftover.Id).ShouldNotBeNull(), "sh");

        // A process running under a pid the killed Fleet recorded for a harness that has since exited.
        var reused = Sleep();
        new HarnessProcessRecords(records, NullLogger.Instance) { Owner = killedFleetIdentity }
            .Add(ProcessIdentity.Read(reused.Id).ShouldNotBeNull() with { StartTime = 1 }, "opencode");

        // A harness of a Fleet that's still running.
        var runningFleet = Sleep();
        var othersHarness = Sleep();
        new HarnessProcessRecords(records, NullLogger.Instance) { Owner = ProcessIdentity.Read(runningFleet.Id) }
            .Add(ProcessIdentity.Read(othersHarness.Id).ShouldNotBeNull(), "opencode");

        // And one no Fleet recorded.
        var unrecorded = Sleep();

        await using (var fleet = new Factory(Path.Combine(_root, "fleet.db"), records))
        {
            _ = fleet.Services;

            await leftover.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilGoneAsync(leftoverChild, TimeSpan.FromSeconds(10));
            reused.HasExited.ShouldBeFalse();
            othersHarness.HasExited.ShouldBeFalse();
            unrecorded.HasExited.ShouldBeFalse();

            // The stale records are gone; the running Fleet's stays for it. (Any this test process's other Fleets
            // write now are theirs.)
            Directory.GetFiles(records).Select(Path.GetFileName)
                .Where(name => !name!.StartsWith($"{Environment.ProcessId}-", StringComparison.Ordinal))
                .ShouldHaveSingleItem().ShouldStartWith($"{runningFleet.Id}-");
        }
    }

    private Process Sleep() => Start("sleep", redirect: false, "60");

    private Process Start(string fileName, bool redirect, params string[] arguments)
    {
        var psi = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardOutput = redirect };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        var process = Process.Start(psi)!;
        _started.Add(process);
        return process;
    }

    private static async Task WaitUntilGoneAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (ProcessIdentity.Read(pid) is not null)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Process {pid} is still running.");
            await Task.Delay(50);
        }
    }

    private sealed class Factory(string dbPath, string records) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Fleet:DatabasePath", dbPath);
            builder.UseSetting("Fleet:AnalyticsEnabled", "false");
            builder.UseSetting("Fleet:Auth:Enabled", "false");
            builder.UseSetting("Fleet:Harness:ProcessRecordsDirectory", records);
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(d => d.ImplementationType == typeof(OpenCodeWarmupHostedService)).ToList())
                    services.Remove(descriptor);
            });
        }
    }
}
