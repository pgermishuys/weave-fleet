namespace WeaveFleet.Application.Services;

/// <summary>
/// Runs git the way Fleet does for worktrees: no shell, never waiting on a prompt nobody can see, and a failure as
/// one sentence from git's own error.
/// </summary>
internal static class GitCommand
{
    /// <summary>Runs git in <paramref name="workingDir"/> and returns what it printed.</summary>
    /// <param name="timeout">Null waits as long as git takes.</param>
    /// <exception cref="GitCommandException">Git exited non-zero, or took longer than <paramref name="timeout"/>.</exception>
    public static async Task<string> RunAsync(string workingDir, TimeSpan? timeout, params string[] args)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        // Never wait on a credential prompt nobody can see.
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        process.Start();
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = timeout is { } limit ? new CancellationTokenSource(limit) : new CancellationTokenSource();
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
            throw new GitCommandException(string.Join(' ', args), $"timed out after {timeout!.Value.TotalSeconds:0} seconds");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new GitCommandException(string.Join(' ', args), CleanGitMessage(stderr));

        return stdout;
    }

    /// <summary>
    /// Runs a long git command (a clone) and hands each progress line git writes to <paramref name="onProgress"/>
    /// as it comes. Git redraws a progress line with a carriage return, so that ends a line too.
    /// </summary>
    /// <param name="environment">Extra variables for git's process only, such as one-off config.</param>
    /// <exception cref="GitCommandException">Git exited non-zero.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled; git is stopped first.
    /// </exception>
    public static async Task RunWithProgressAsync(
        string workingDir,
        IReadOnlyDictionary<string, string>? environment,
        Action<string> onProgress,
        CancellationToken ct,
        params string[] args)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
            process.StartInfo.Environment[key] = value;

        process.Start();
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = new System.Text.StringBuilder();
        var stderrTask = ReadLinesAsync(process.StandardError, line =>
        {
            stderr.AppendLine(line);
            onProgress(line);
        });

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        await stdoutTask;
        await stderrTask;
        if (process.ExitCode != 0)
            throw new GitCommandException(string.Join(' ', args), CleanGitMessage(stderr.ToString()));
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> onLine)
    {
        var buffer = new char[1024];
        var line = new System.Text.StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c is '\r' or '\n')
                {
                    if (line.Length > 0)
                        onLine(line.ToString().Trim());
                    line.Clear();
                }
                else
                {
                    line.Append(c);
                }
            }
        }

        if (line.Length > 0)
            onLine(line.ToString().Trim());
    }

    /// <summary>
    /// Turns git's stderr into one readable sentence. Git also writes progress there
    /// ("Preparing worktree …"), so when it marks lines with "fatal:" or "error:", only those count.
    /// </summary>
    private static string CleanGitMessage(string stderr)
    {
        string[] prefixes = ["fatal: ", "error: "];
        var lines = ParseLines(stderr).Where(line => !line.StartsWith("hint:", StringComparison.Ordinal)).ToArray();
        var marked = lines
            .Where(line => prefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(line => line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..])
            .ToArray();
        var message = marked.Length > 0 ? marked : lines;
        return message.Length > 0 ? string.Join(' ', message) : "git exited with an error";
    }

    public static string[] ParseLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>A git command that exited non-zero; <see cref="GitMessage"/> is fit to show the user.</summary>
internal sealed class GitCommandException(string command, string gitMessage)
    : Exception($"git {command} failed: {gitMessage}")
{
    public string GitMessage { get; } = gitMessage;
}
