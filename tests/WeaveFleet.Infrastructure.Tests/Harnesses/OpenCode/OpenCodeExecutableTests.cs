using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class OpenCodeExecutableTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fleet-opencode-exe-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void OpenCode_2_is_not_working_and_says_why()
    {
        var result = OpenCodeExecutable.RejectOpenCode2(HarnessAvailability.Ready("2.0.8", "/home/me/.opencode/bin/opencode"));

        result.Available.ShouldBeFalse();
        result.State.ShouldBe(HarnessStates.NotWorking);
        result.Version.ShouldBe("2.0.8");
        result.ExecutablePath.ShouldBe("/home/me/.opencode/bin/opencode");
        result.Reason.ShouldBe(
            "/home/me/.opencode/bin/opencode is OpenCode 2 (2.0.8), and the OpenCode harness needs OpenCode 1. " +
            "Installing OpenCode 1 replaces an OpenCode 2 in ~/.opencode/bin, where both install. " +
            "To keep OpenCode 2 as well, install it again in its own folder afterwards (Settings → Harnesses → OpenCode 2).");
    }

    [Theory]
    [InlineData("1.18.31")]
    [InlineData("1.99.0")]
    public void OpenCode_1_stays_ready(string version)
    {
        var ready = HarnessAvailability.Ready(version, "/usr/bin/opencode");

        OpenCodeExecutable.RejectOpenCode2(ready).ShouldBeSameAs(ready);
    }

    [Theory]
    [InlineData("2.0.0")]
    [InlineData("2.0.8")]
    [InlineData("3.1.0")]
    public void Versions_from_2_0_0_are_OpenCode_2(string version)
    {
        OpenCodeExecutable.IsOpenCode2(version).ShouldBeTrue();
    }

    [Fact]
    public void An_install_that_is_not_ready_is_left_as_it_is()
    {
        var notInstalled = HarnessAvailability.NotInstalled("OpenCode isn't installed.");

        OpenCodeExecutable.RejectOpenCode2(notInstalled).ShouldBeSameAs(notInstalled);
    }

    [Fact]
    public async Task Fleet_refuses_to_start_OpenCode_2()
    {
        if (OperatingSystem.IsWindows()) return;
        var opencode = Script("opencode", "echo 'opencode v2.0.8'");

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => OpenCodeExecutable.RejectOpenCode2Async(opencode, CancellationToken.None));

        error.Message.ShouldStartWith($"{opencode} is OpenCode 2 (2.0.8)");
    }

    [Fact]
    public async Task Fleet_refuses_OpenCode_2_behind_a_link()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Script("opencode.exe", "echo 'opencode v2.0.8'");
        var link = Path.Combine(_root, "opencode");
        File.CreateSymbolicLink(link, target);

        await Should.ThrowAsync<InvalidOperationException>(
            () => OpenCodeExecutable.RejectOpenCode2Async(link, CancellationToken.None));
    }

    [Fact]
    public async Task Fleet_starts_OpenCode_1()
    {
        if (OperatingSystem.IsWindows()) return;
        var opencode = Script("opencode", "echo 1.18.31");

        await OpenCodeExecutable.RejectOpenCode2Async(opencode, CancellationToken.None);
    }

    [Fact]
    public async Task A_missing_executable_is_left_to_the_start_to_report()
    {
        await OpenCodeExecutable.RejectOpenCode2Async("opencode", CancellationToken.None);
        await OpenCodeExecutable.RejectOpenCode2Async(Path.Combine(_root, "missing"), CancellationToken.None);
    }

    [Fact]
    public async Task An_update_to_OpenCode_2_in_place_is_noticed()
    {
        if (OperatingSystem.IsWindows()) return;
        var opencode = Script("opencode", "echo 1.18.31");
        await OpenCodeExecutable.RejectOpenCode2Async(opencode, CancellationToken.None);

        Script("opencode", "echo 'opencode v2.0.8'");
        File.SetLastWriteTimeUtc(opencode, DateTime.UtcNow.AddMinutes(1));

        await Should.ThrowAsync<InvalidOperationException>(
            () => OpenCodeExecutable.RejectOpenCode2Async(opencode, CancellationToken.None));
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
