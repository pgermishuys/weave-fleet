using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

public sealed class HarnessProbeTests : IDisposable
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(1);

    private readonly string _root = Directory.CreateTempSubdirectory("fleet-probe-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_missing_executable_is_not_installed()
    {
        var result = await HarnessProbe.CheckInstalledAsync("OpenCode", "opencode", executablePath: null, NullLogger.Instance, CancellationToken.None);

        result.Available.ShouldBeFalse();
        result.State.ShouldBe(HarnessStates.NotInstalled);
        result.Reason.ShouldBe("OpenCode isn't installed: Fleet couldn't find opencode on PATH or in the folders its installer uses.");
    }

    [Fact]
    public async Task A_configured_path_with_no_file_is_not_installed()
    {
        var missing = Path.Combine(_root, "claude");

        var result = await HarnessProbe.CheckInstalledAsync("Claude Code", missing, [], NullLogger.Instance, CancellationToken.None);

        result.State.ShouldBe(HarnessStates.NotInstalled);
        result.Reason.ShouldBe($"Claude Code isn't installed: there's no file at {missing}.");
    }

    [Fact]
    public async Task An_executable_that_prints_its_version_is_ready()
    {
        if (OperatingSystem.IsWindows()) return;
        var script = Script("opencode", "echo 1.18.30");

        var result = await HarnessProbe.CheckInstalledAsync("OpenCode", "opencode", script, NullLogger.Instance, CancellationToken.None);

        result.Available.ShouldBeTrue();
        result.State.ShouldBe(HarnessStates.Ready);
        result.Version.ShouldBe("1.18.30");
        result.ExecutablePath.ShouldBe(script);
        result.Reason.ShouldBeNull();
    }

    [Fact]
    public async Task An_executable_that_fails_is_not_working_and_says_where_it_is()
    {
        if (OperatingSystem.IsWindows()) return;
        var script = Script("opencode", "echo broken >&2; exit 3");

        var result = await HarnessProbe.CheckInstalledAsync("OpenCode", "opencode", script, NullLogger.Instance, CancellationToken.None);

        result.Available.ShouldBeFalse();
        result.State.ShouldBe(HarnessStates.NotWorking);
        result.Reason.ShouldBe("opencode --version exited with code 3.");
        result.ExecutablePath.ShouldBe(script);
    }

    [Fact]
    public async Task An_executable_that_never_finishes_is_not_working()
    {
        if (OperatingSystem.IsWindows()) return;
        var script = Script("opencode", "sleep 5");

        var result = await HarnessProbe.CheckInstalledAsync("OpenCode", "opencode", script, NullLogger.Instance, CancellationToken.None, ShortTimeout);

        result.State.ShouldBe(HarnessStates.NotWorking);
        result.Reason.ShouldBe("opencode --version didn't finish within 1 seconds.");
    }

    // On a busy machine (the iOS walk-through's runner: load averages of 100 to 300) a cold start of OpenCode's binary
    // sometimes takes 8 to 27 s, and the New session screen then said "No harness" for minutes.
    [Fact]
    public async Task An_executable_that_answered_before_and_is_only_slow_now_stays_ready()
    {
        if (OperatingSystem.IsWindows()) return;
        var answered = Path.Combine(_root, "answered");
        var script = Script("opencode", $"[ -f '{answered}' ] && sleep 5; touch '{answered}'; echo 1.18.31");

        await HarnessProbe.CheckInstalledAsync("OpenCode", "opencode", script, NullLogger.Instance, CancellationToken.None, ShortTimeout);
        var slow = await HarnessProbe.CheckInstalledAsync("OpenCode", "opencode", script, NullLogger.Instance, CancellationToken.None, ShortTimeout);

        slow.Available.ShouldBeTrue();
        slow.State.ShouldBe(HarnessStates.Ready);
        slow.Version.ShouldBe("1.18.31");
        slow.ExecutablePath.ShouldBe(script);
    }

    [Fact]
    public async Task An_executable_replaced_since_it_answered_is_not_working_when_it_never_finishes()
    {
        if (OperatingSystem.IsWindows()) return;
        var script = Script("opencode", "echo 1.18.31");
        await HarnessProbe.CheckInstalledAsync("OpenCode", "opencode", script, NullLogger.Instance, CancellationToken.None, ShortTimeout);

        Script("opencode", "sleep 5");
        File.SetLastWriteTimeUtc(script, DateTime.UtcNow.AddMinutes(1));
        var result = await HarnessProbe.CheckInstalledAsync("OpenCode", "opencode", script, NullLogger.Instance, CancellationToken.None, ShortTimeout);

        result.State.ShouldBe(HarnessStates.NotWorking);
    }

    [Fact]
    public async Task An_executable_that_failed_since_it_answered_is_not_working_when_it_never_finishes()
    {
        if (OperatingSystem.IsWindows()) return;
        var runs = Path.Combine(_root, "runs");
        // Answers, then exits 3, then hangs.
        var script = Script("opencode", $"echo x >> '{runs}'; n=$(wc -l < '{runs}'); [ $n -eq 1 ] && echo 1.18.31 && exit 0; [ $n -eq 2 ] && exit 3; sleep 5");

        await HarnessProbe.CheckInstalledAsync("OpenCode", "opencode", script, NullLogger.Instance, CancellationToken.None, ShortTimeout);
        var failed = await HarnessProbe.CheckInstalledAsync("OpenCode", "opencode", script, NullLogger.Instance, CancellationToken.None, ShortTimeout);
        var hung = await HarnessProbe.CheckInstalledAsync("OpenCode", "opencode", script, NullLogger.Instance, CancellationToken.None, ShortTimeout);

        failed.Reason.ShouldBe("opencode --version exited with code 3.");
        hung.State.ShouldBe(HarnessStates.NotWorking);
    }

    [Fact]
    public async Task A_file_that_cannot_run_is_not_working()
    {
        if (OperatingSystem.IsWindows()) return;
        var notExecutable = Path.Combine(_root, "opencode");
        File.WriteAllText(notExecutable, "not a program");

        var result = await HarnessProbe.CheckInstalledAsync("OpenCode", "opencode", notExecutable, NullLogger.Instance, CancellationToken.None);

        result.State.ShouldBe(HarnessStates.NotWorking);
        result.Reason.ShouldNotBeNull();
        result.Reason.ShouldStartWith($"Fleet couldn't run {notExecutable}");
    }

    [Theory]
    [InlineData("1.18.30\n", "1.18.30")]
    [InlineData("2.1.276 (Claude Code)\n", "2.1.276")]
    [InlineData("opencode v0.15.2-beta.1", "0.15.2-beta.1")]
    [InlineData("pi 0.9", "0.9")]
    [InlineData("no version here", null)]
    public void Reads_the_version_from_version_output(string output, string? expected)
    {
        HarnessProbe.ParseVersion(output).ShouldBe(expected);
    }

    private string Script(string name, string body)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
