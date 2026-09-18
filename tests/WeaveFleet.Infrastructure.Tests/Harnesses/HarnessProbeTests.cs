using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

public sealed class HarnessProbeTests : IDisposable
{
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
