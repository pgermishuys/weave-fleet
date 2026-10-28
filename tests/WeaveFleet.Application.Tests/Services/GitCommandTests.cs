using Shouldly;
using WeaveFleet.Application.Git;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// How Fleet runs git: it never waits forever, and stopping it leaves nothing behind. Each test runs a shell alias,
/// so git starts a child of its own the way a hook or credential helper does.
/// </summary>
public sealed class GitCommandTests
{
    [Fact]
    public async Task A_git_that_runs_past_its_time_is_stopped_with_everything_it_started()
    {
        if (OperatingSystem.IsWindows())
            return;

        var marker = Marker();
        var ran = GitCommand.ExecAsync(Path.GetTempPath(), Alias($"sleep {marker}"), TimeSpan.FromSeconds(2), CancellationToken.None);

        var error = await Should.ThrowAsync<GitCommandException>(ran);

        error.GitMessage.ShouldBe("didn't finish within 2 seconds, so Fleet stopped it");
        await WaitUntilGoneAsync(marker);
    }

    [Fact]
    public async Task A_cancelled_git_is_stopped_with_everything_it_started()
    {
        if (OperatingSystem.IsWindows())
            return;

        var marker = Marker();
        using var cancel = new CancellationTokenSource();
        var ran = GitCommand.ExecAsync(Path.GetTempPath(), Alias($"sleep {marker}"), TimeSpan.FromMinutes(1), cancel.Token);
        await WaitUntilAsync(() => Running(marker));

        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(ran);
        await WaitUntilGoneAsync(marker);
    }

    [Fact]
    public async Task Git_that_writes_a_lot_of_errors_does_not_stall_on_a_full_pipe()
    {
        if (OperatingSystem.IsWindows())
            return;

        var result = await GitCommand.ExecAsync(
            Path.GetTempPath(), Alias("yes e | head -c 300000 >&2"), TimeSpan.FromSeconds(20), CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.StandardError.Length.ShouldBe(300000);
    }

    [Fact]
    public async Task Git_has_nothing_to_read_so_it_never_waits_for_input()
    {
        if (OperatingSystem.IsWindows())
            return;

        var result = await GitCommand.ExecAsync(Path.GetTempPath(), Alias("cat"), TimeSpan.FromSeconds(10), CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_git_whose_leftover_holds_its_output_open_does_not_hang_the_caller()
    {
        if (OperatingSystem.IsWindows())
            return;

        var marker = Marker();
        try
        {
            var ran = GitCommand.ExecAsync(
                Path.GetTempPath(), Alias($"sleep {marker} & echo started"), TimeSpan.FromMinutes(1), CancellationToken.None);

            var error = await Should.ThrowAsync<GitCommandException>(ran);

            error.GitMessage.ShouldBe("exited, but something it started kept its output open");
        }
        finally
        {
            foreach (var pid in Pids(marker))
                System.Diagnostics.Process.GetProcessById(pid).Kill();
        }
    }

    [Fact]
    public async Task A_failing_command_says_why_in_one_sentence()
    {
        var folder = Directory.CreateTempSubdirectory("fleet-git-").FullName;
        try
        {
            var error = await Should.ThrowAsync<GitCommandException>(
                GitCommand.RunAsync(folder, TimeSpan.FromSeconds(10), "rev-parse", "HEAD"));

            error.GitMessage.ShouldContain("not a git repository");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Git_never_calls_a_login_program_from_the_environment()
    {
        if (OperatingSystem.IsWindows())
            return;

        // A server that wants a login makes git ask: git tries GIT_ASKPASS / SSH_ASKPASS before it reads GIT_TERMINAL_PROMPT.
        var folder = Directory.CreateTempSubdirectory("fleet-askpass-").FullName;
        var called = Path.Combine(folder, "called");
        var askpass = Path.Combine(folder, "askpass.sh");
        await File.WriteAllTextAsync(askpass, $"#!/bin/sh\necho asked >> '{called}'\necho secret\n");
        File.SetUnixFileMode(askpass, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var listener = new System.Net.HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                var context = await listener.GetContextAsync();
                context.Response.StatusCode = 401;
                context.Response.AddHeader("WWW-Authenticate", "Basic realm=\"fleet\"");
                context.Response.Close();
            }
        });

        var saved = (Environment.GetEnvironmentVariable("GIT_ASKPASS"), Environment.GetEnvironmentVariable("SSH_ASKPASS"));
        Environment.SetEnvironmentVariable("GIT_ASKPASS", askpass);
        Environment.SetEnvironmentVariable("SSH_ASKPASS", askpass);
        try
        {
            var error = await Should.ThrowAsync<GitCommandException>(
                GitCommand.RunAsync(folder, TimeSpan.FromSeconds(20), "ls-remote", $"http://127.0.0.1:{port}/repo.git"));

            File.Exists(called).ShouldBeFalse($"git called the askpass program: {error.GitMessage}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_ASKPASS", saved.Item1);
            Environment.SetEnvironmentVariable("SSH_ASKPASS", saved.Item2);
            listener.Stop();
            try { await serving; } catch (Exception) { }
            Directory.Delete(folder, recursive: true);
        }
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string[] Alias(string shell) => ["-c", $"alias.fleet-test=!{shell}", "fleet-test"];

    /// <summary>A sleep length no other process on the machine is using, so the test can find its own.</summary>
    private static string Marker() => $"{Random.Shared.Next(1000, 9999)}.{Random.Shared.Next(100, 999)}";

    private static bool Running(string marker) => Pids(marker).Count > 0;

    /// <summary>Processes running <c>sleep {marker}</c>, from their command lines (Linux) or <c>ps</c> (macOS).</summary>
    private static List<int> Pids(string marker)
    {
        var pids = new List<int>();
        if (OperatingSystem.IsLinux())
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid))
                    continue;
                try
                {
                    var args = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0');
                    if (args.Length >= 2 && Path.GetFileName(args[0]) == "sleep" && args[1] == marker)
                        pids.Add(pid);
                }
                catch (IOException)
                {
                    // Exited while being read.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return pids;
        }

        using var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ps", "-axo pid=,command=")
        {
            RedirectStandardOutput = true,
        })!;
        foreach (var line in ps.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1] == $"sleep {marker}" && int.TryParse(parts[0], out var pid))
                pids.Add(pid);
        }

        ps.WaitForExit();
        return pids;
    }

    private static Task WaitUntilGoneAsync(string marker) => WaitUntilAsync(() => !Running(marker));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        condition().ShouldBeTrue();
    }
}
