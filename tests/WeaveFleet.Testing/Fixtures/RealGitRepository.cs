using System.Diagnostics;

namespace WeaveFleet.Testing.Fixtures;

/// <summary>
/// A throwaway git repository at <c>{ParentPath}/repo</c>, on branch <c>main</c> with one empty commit,
/// for tests that need real git (worktrees, remotes). Everything under <see cref="ParentPath"/> is
/// deleted on dispose.
/// </summary>
public sealed class RealGitRepository : IDisposable
{
    private readonly List<string> _worktreePaths = [];

    public RealGitRepository()
    {
        ParentPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fleet-root-{Guid.NewGuid():N}");
        Path = System.IO.Path.Combine(ParentPath, "repo");
        Directory.CreateDirectory(Path);

        Run(Path, "init", "--initial-branch=main");
        ConfigureIdentity(Path);
        Git("commit", "--allow-empty", "-m", "initial");
    }

    public string ParentPath { get; }
    public string Path { get; }

    /// <summary>Runs git in the repository and returns its standard output.</summary>
    public string Git(params string[] args) => Run(Path, args);

    /// <summary>The commit a ref points at, e.g. <c>HEAD</c> or <c>main</c>.</summary>
    public string CommitOf(string rev) => Git("rev-parse", rev).Trim();

    /// <summary>Adds a linked worktree on a new branch at <c>{ParentPath}/repo-worktrees/{branch}</c>.</summary>
    public string CreateWorktree(string branchName)
    {
        var worktreeDir = System.IO.Path.Combine(ParentPath, "repo-worktrees", branchName);
        Git("worktree", "add", worktreeDir, "-b", branchName);
        _worktreePaths.Add(worktreeDir);
        return worktreeDir;
    }

    /// <summary>
    /// Gives the repository an <c>origin</c> (a bare copy under <see cref="ParentPath"/>), fetched once,
    /// and returns a function that pushes a new commit to origin's <c>main</c> without fetching it here.
    /// </summary>
    public Func<string, string> AddOrigin()
    {
        var remotePath = System.IO.Path.Combine(ParentPath, "origin.git");
        Run(ParentPath, "clone", "--bare", Path, remotePath);
        Git("remote", "add", "origin", remotePath);
        Git("fetch", "origin");
        Git("remote", "set-head", "origin", "main");

        return message => PushToOrigin(message);
    }

    /// <summary>
    /// Pushes a new commit to <paramref name="branch"/> on origin (created from origin's main when it's new)
    /// without fetching it here, and returns the commit. Needs <see cref="AddOrigin"/> first.
    /// </summary>
    public string PushToOrigin(string message, string branch = "main")
    {
        var remotePath = System.IO.Path.Combine(ParentPath, "origin.git");
        var otherPath = System.IO.Path.Combine(ParentPath, $"other-{Guid.NewGuid():N}");
        Run(ParentPath, "clone", remotePath, otherPath);
        ConfigureIdentity(otherPath);
        Run(otherPath, "checkout", "-B", branch, $"origin/{(RemoteBranchExists(otherPath, branch) ? branch : "main")}");
        Run(otherPath, "commit", "--allow-empty", "-m", message);
        Run(otherPath, "push", "origin", branch);
        return Run(otherPath, "rev-parse", "HEAD").Trim();
    }

    private static bool RemoteBranchExists(string clonePath, string branch)
    {
        try
        {
            Run(clonePath, "show-ref", "--verify", "--quiet", $"refs/remotes/origin/{branch}");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        // Remove worktrees first to release git locks
        foreach (var wt in _worktreePaths)
        {
            try { Git("worktree", "remove", "--force", wt); } catch { }
        }
        try { Git("worktree", "prune"); } catch { }

        if (!Directory.Exists(ParentPath))
            return;

        // Git objects on Windows are often read-only; clear attributes before deletion
        foreach (var file in Directory.EnumerateFiles(ParentPath, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }

        for (var i = 0; i < 3; i++)
        {
            try
            {
                Directory.Delete(ParentPath, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (i < 2)
            {
                Thread.Sleep(100);
            }
            catch (IOException) when (i < 2)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void ConfigureIdentity(string repoPath)
    {
        Run(repoPath, "config", "user.email", "test@test.com");
        Run(repoPath, "config", "user.name", "Test");
        Run(repoPath, "config", "commit.gpgsign", "false");
    }

    private static string Run(string workDir, params string[] args)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            proc.StartInfo.ArgumentList.Add(arg);
        proc.Start();
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Result}");
        return stdout.Result;
    }
}
