using System.Diagnostics;
using Shouldly;

namespace WeaveFleet.IntegrationTests.Harnesses;

/// <summary>
/// Makes a live test's folder a git repository, as a run's worktree is, so the files check's commit can be seen.
/// </summary>
internal static class WorkflowLiveGit
{
    /// <summary>A repository with one commit and its own identity; files already in the folder stay untracked.</summary>
    public static void Init(string folder)
    {
        Run(folder, "init", "--quiet");
        Run(folder, "config", "user.name", "Fleet Live Tests");
        Run(folder, "config", "user.email", "live-tests@fleet.invalid");
        Run(folder, "config", "commit.gpgsign", "false");
        Run(folder, "commit", "--quiet", "--allow-empty", "-m", "first");
    }

    public static string Run(string folder, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, $"git {string.Join(' ', args)}: {error}");
        return output;
    }
}
