using System.ComponentModel;
using System.Diagnostics;
using WeaveFleet.Infrastructure.IO;
using WeaveFleet.Infrastructure.Runtimes;

namespace WeaveFleet.Infrastructure.Tests.IO;

/// <summary>
/// Fleet writes an executable, then starts it. A process another thread starts at that moment is forked holding a copy
/// of Fleet's write handle to the new file until it execs, and while any write handle is open the kernel refuses to
/// exec the file ("Text file busy", ETXTBSY). These tests write and start executables while other threads keep
/// starting processes, which made about 1 start in 40 fail before <see cref="FreshExecutable"/> retried.
/// </summary>
public sealed class FreshExecutableTests : IDisposable
{
    private const int Starts = 800;

    /// <summary>
    /// Fewer, because a probe takes about 90 ms. Before the fix 15 of 800 probes failed, so 150 failed at least once
    /// about 19 runs in 20; the plain starts above are the reliable reproducer.
    /// </summary>
    private const int Probes = 150;
    private const int Forkers = 4;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fleet-fresh-exec-{Guid.NewGuid():N}");

    public FreshExecutableTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task A_just_written_executable_starts_while_other_threads_start_processes()
    {
        if (OperatingSystem.IsWindows()) return;

        var failures = await WhileForkingAsync(() =>
        {
            var failed = 0;
            for (var i = 0; i < Starts; i++)
            {
                var path = WriteScript($"run-{i}", "exit 0");
                try
                {
                    using var process = FreshExecutable.Start(new ProcessStartInfo(path) { UseShellExecute = false });
                    process.WaitForExit();
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == FreshExecutable.TextFileBusy)
                {
                    failed++;
                }
            }

            return failed;
        });

        failures.ShouldBe(0);
    }

    [Fact]
    public async Task A_just_written_bun_answers_its_version_while_other_threads_start_processes()
    {
        if (OperatingSystem.IsWindows()) return;

        var errors = await WhileForkingAsync(() =>
        {
            var failed = new List<string>();
            for (var i = 0; i < Probes; i++)
            {
                var path = WriteScript(Path.Combine($"bun-{i}", "bun"), "echo 1.4.5");
                var result = BunVersionProbe.RunAsync(path, TimeSpan.FromSeconds(20), CancellationToken.None).GetAwaiter().GetResult();
                if (result.Error is { } error)
                    failed.Add(error);
            }

            return failed;
        });

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void A_file_still_open_for_writing_fails_as_busy_after_the_retries()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = WriteScript("held", "exit 0");
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write);
        var stopwatch = Stopwatch.StartNew();

        var ex = Should.Throw<Win32Exception>(() => FreshExecutable.Start(new ProcessStartInfo(path) { UseShellExecute = false }));

        ex.NativeErrorCode.ShouldBe(FreshExecutable.TextFileBusy);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    /// <summary>Runs <paramref name="work"/> while <see cref="Forkers"/> threads start <c>true</c> over and over.</summary>
    private static async Task<T> WhileForkingAsync<T>(Func<T> work)
    {
        using var stop = new CancellationTokenSource();
        var forkers = Enumerable.Range(0, Forkers).Select(_ => Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var process = Process.Start(new ProcessStartInfo("/usr/bin/true") { UseShellExecute = false })!;
                process.WaitForExit();
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        try
        {
            return await Task.Factory.StartNew(work, TaskCreationOptions.LongRunning);
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(forkers);
        }
    }

    /// <summary>Writes a shell script the way Fleet writes an executable: from its own process, then chmod.</summary>
    private string WriteScript(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
