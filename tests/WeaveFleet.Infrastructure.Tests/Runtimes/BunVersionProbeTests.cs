using System.Diagnostics;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Infrastructure.Runtimes;

namespace WeaveFleet.Infrastructure.Tests.Runtimes;

public sealed class BunVersionProbeTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fleet-probe-{Guid.NewGuid():N}");

    public BunVersionProbeTests() => Directory.CreateDirectory(_dir);

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

    private string Script(string body, string? folder = null, bool executable = true)
    {
        var directory = folder is null ? _dir : Path.Combine(_dir, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bun");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (executable && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static Task<BunProbeResult> Run(string path, TimeSpan? timeout = null, CancellationToken ct = default) =>
        BunVersionProbe.RunAsync(path, timeout ?? Generous, ct);

    [Fact]
    public void Default_timeout_is_five_seconds() => BunVersionProbe.DefaultTimeout.ShouldBe(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Reads_a_plain_version()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await Run(Script("echo 1.4.5"));

        result.Error.ShouldBeNull();
        result.Version.ShouldBe(new BunVersion(1, 4, 5, null));
    }

    [Theory]
    [InlineData("echo bun 1.4.5")]
    [InlineData("echo v1.4.5")]
    [InlineData("echo 1.4.5; echo 1.4.6")]
    [InlineData("true")]
    public async Task Output_that_is_not_exactly_one_version_is_an_error(string body)
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await Run(Script(body));

        result.Version.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("isn't a version");
    }

    [Fact]
    public async Task The_error_quotes_a_short_clean_excerpt()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await Run(Script("printf 'bun \\033[31m1.4.5 xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx'"));

        result.Error.ShouldNotBeNull();
        result.Error.ShouldNotContain("\u001b");
        result.Error.Length.ShouldBeLessThan(150);
    }

    [Fact]
    public async Task A_failing_exit_code_is_named()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await Run(Script("echo 1.4.5; exit 3"));

        result.Version.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("3");
    }

    [Fact]
    public async Task A_bun_that_hangs_is_killed_with_its_children_when_the_time_is_up()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Script("sleep 31 &\nsleep 32");

        var clock = Stopwatch.StartNew();
        var result = await Run(path, TimeSpan.FromMilliseconds(300));
        clock.Stop();

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        result.Version.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("didn't answer within");
        await Task.Delay(300);
        StillRunning("sleep 31").ShouldBeFalse();
        StillRunning("sleep 32").ShouldBeFalse();
    }

    [Fact]
    public async Task A_bun_that_floods_output_does_not_hang_or_pass()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await Run(Script("yes 1.4.5 & yes err >&2 & wait"), TimeSpan.FromSeconds(10));

        result.Version.ShouldBeNull();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_file_that_is_not_executable_is_an_error_not_a_throw()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Script("echo 1.4.5", executable: false);
        var result = await Run(path);

        result.Version.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldBe($"Bun at {path} isn't executable (chmod +x {path}).");
    }

    [Theory]
    [InlineData("echo 1.4.5; (sleep 30 &) ; exit 0")]
    [InlineData("sleep 30 & echo 1.4.5")]
    public async Task A_version_printed_before_a_child_keeps_stdout_open_is_still_read(string body)
    {
        if (OperatingSystem.IsWindows()) return;
        var watch = Stopwatch.StartNew();
        var result = await Run(Script(body), TimeSpan.FromSeconds(5));

        result.Error.ShouldBeNull();
        result.Version.ShouldBe(new BunVersion(1, 4, 5, null));
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task The_probe_runs_in_an_empty_temporary_folder_that_is_deleted_afterwards()
    {
        if (OperatingSystem.IsWindows()) return;
        var record = Path.Combine(_dir, "cwd.txt");
        var path = Script($"pwd > '{record}'; ls -A . >> '{record}'; echo 1.4.5", folder: "bunfolder");
        File.WriteAllText(Path.Combine(_dir, "bunfolder", "neighbour.txt"), "x");

        var result = await Run(path);

        result.Error.ShouldBeNull();
        var lines = await File.ReadAllLinesAsync(record);
        lines.Length.ShouldBe(1, "the folder holds nothing, not even the bun's neighbours");
        lines[0].ShouldNotBe(Path.Combine(_dir, "bunfolder"));
        lines[0].ShouldStartWith(Path.GetTempPath().TrimEnd('/'));
        Directory.Exists(lines[0]).ShouldBeFalse();
    }

    [Fact]
    public async Task A_folder_with_shell_characters_in_its_name_is_safe()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Script("echo 1.4.5", folder: "a b;c$(touch pwned)");

        var result = await Run(path);

        result.Version.ShouldBe(new BunVersion(1, 4, 5, null));
        Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ShouldBe(["bun"]);
        File.Exists("pwned").ShouldBeFalse();
    }

    [Fact]
    public async Task Fleets_environment_is_not_passed_on()
    {
        if (OperatingSystem.IsWindows()) return;
        var name = $"FLEET_BUN_PROBE_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "x");
        try
        {
            var result = await Run(Script($"if [ -z \"${{{name}+set}}\" ]; then echo 1.4.5; else echo leaked; fi"));

            result.Error.ShouldBeNull();
            result.Version.ShouldBe(new BunVersion(1, 4, 5, null));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task HOME_and_USER_are_not_passed_on()
    {
        if (OperatingSystem.IsWindows()) return;
        // /bin/sh may invent a PATH, but it doesn't invent HOME or USER.
        var result = await Run(Script("if [ -z \"${HOME+set}\" ] && [ -z \"${USER+set}\" ]; then echo 1.4.5; else echo leaked; fi"));

        result.Version.ShouldBe(new BunVersion(1, 4, 5, null));
    }

    [Fact]
    public async Task Cancelling_throws_and_kills_the_process()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Script("sleep 33 &\nsleep 34");
        using var cts = new CancellationTokenSource();
        var task = Run(path, Generous, cts.Token);
        await Task.Delay(500);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(task);
        await Task.Delay(300);
        StillRunning("sleep 33").ShouldBeFalse();
        StillRunning("sleep 34").ShouldBeFalse();
    }

    [Fact]
    public async Task A_missing_file_is_an_error_not_a_throw()
    {
        var result = await Run(Path.Combine(_dir, "nothing", OperatingSystem.IsWindows() ? "bun.exe" : "bun"));

        result.Version.ShouldBeNull();
        result.Error.ShouldNotBeNull();
    }

    private static bool StillRunning(string commandLine)
    {
        using var ps = Process.Start(new ProcessStartInfo("pgrep", ["-f", "-x", commandLine])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        ps.WaitForExit();
        return ps.ExitCode == 0;
    }
}
