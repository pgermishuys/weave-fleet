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
