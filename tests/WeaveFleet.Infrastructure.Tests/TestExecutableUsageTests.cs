using System.Diagnostics;

namespace WeaveFleet.Infrastructure.Tests;

/// <summary>
/// A test that writes a script and starts it must write it through <see cref="TestExecutable"/>. Writing it in this
/// process leaves a write handle that a process another test forks inherits, and starting the file then fails with
/// "Text file busy" (ETXTBSY): the flake that failed main's CI on 27 November 2026. SessionFileWriteTests is left out:
/// it checks permissions and inodes and never runs the file.
/// </summary>
public sealed class TestExecutableUsageTests : IDisposable
{
    private static readonly string[] Projects = ["WeaveFleet.Infrastructure.Tests", "WeaveFleet.Application.Tests"];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fleet-test-exec-{Guid.NewGuid():N}");

    public TestExecutableUsageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void No_test_makes_a_file_executable_itself()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "tests", "WeaveFleet.Infrastructure.Tests")))
            root = Path.GetDirectoryName(root);
        root.ShouldNotBeNull();

        var offenders = Projects
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, "tests", project), "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(file => Path.GetFileName(file) is not ("TestExecutable.cs" or "TestExecutableUsageTests.cs" or "FreshExecutableTests.cs" or "SessionFileWriteTests.cs"))
            .SelectMany(file => File.ReadLines(file).Select((line, index) => (file, line, number: index + 1)))
            .Where(x => x.line.Contains("SetUnixFileMode") && x.line.Contains("UserWrite") && x.line.Contains("UserExecute"))
            .Select(x => $"{Path.GetRelativePath(root, x.file)}:{x.number}")
            .ToList();

        offenders.ShouldBeEmpty("Write a script a test runs with TestExecutable.Write, not File.WriteAllText + SetUnixFileMode.");
    }

    [Fact]
    public async Task A_script_written_with_TestExecutable_starts_while_other_threads_start_processes()
    {
        if (OperatingSystem.IsWindows()) return;

        using var stop = new CancellationTokenSource();
        var forkers = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var p = Process.Start(new ProcessStartInfo("/usr/bin/true") { UseShellExecute = false })!;
                p.WaitForExit();
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        var failed = 0;
        try
        {
            await Task.Factory.StartNew(() =>
            {
                for (var i = 0; i < 400; i++)
                {
                    var path = Path.Combine(_dir, $"run-{i}");
                    TestExecutable.Write(path, "#!/bin/sh\nexit 0\n");
                    try
                    {
                        using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false })!;
                        p.WaitForExit();
                    }
                    catch (System.ComponentModel.Win32Exception) { failed++; }
                }
            }, TaskCreationOptions.LongRunning);
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(forkers);
        }

        failed.ShouldBe(0);
    }
}
