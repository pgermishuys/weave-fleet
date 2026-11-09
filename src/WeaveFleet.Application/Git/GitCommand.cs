using System.Text;
using System.Diagnostics;
using System.Globalization;

namespace WeaveFleet.Application.Git;

/// <summary>
/// Runs git the way Fleet does: no shell, never waiting on a prompt nobody can see, never for longer than the caller
/// allows, and a failure as one sentence from git's own error. Git that runs out of time or is cancelled is stopped
/// with everything it started (a hook, a credential helper, Git LFS), so nothing is left running behind it.
/// </summary>
internal static class GitCommand
{
    /// <summary>How long git gets to exit once it has been killed.</summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long git's output may stay open after git exits. A process it started and left running (a hook's
    /// background job) can hold it open for good.
    /// </summary>
    private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(5);

    /// <summary>Runs git in <paramref name="workingDir"/> and returns what it printed.</summary>
    /// <exception cref="GitCommandException">Git exited non-zero, or took longer than <paramref name="timeout"/>.</exception>
    public static async Task<string> RunAsync(string workingDir, TimeSpan timeout, params string[] args)
    {
        var result = await ExecAsync(workingDir, args, timeout, CancellationToken.None).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new GitCommandException(string.Join(' ', args), CleanGitMessage(result.StandardError));

        return result.StandardOutput;
    }

    /// <summary>
    /// Runs git in <paramref name="workingDir"/> and returns its exit code and everything it printed, whatever the
    /// exit code. Git reads nothing: its input is closed.
    /// </summary>
    /// <param name="environment">Extra variables for git's process only.</param>
    /// <exception cref="GitCommandException">Git took longer than <paramref name="timeout"/>, and was stopped.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; git is stopped first.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Git isn't installed.</exception>
    public static async Task<GitCommandResult> ExecAsync(
        string workingDir,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = new Process();
        process.StartInfo = StartInfo(workingDir, args, environment);
        process.Start();
        process.StandardInput.Close();

        // Both streams are read as git writes them: git blocks once a pipe nobody reads is full.
        // Stdout is read as bytes and decoded here: a text reader would swallow a UTF-8 byte order mark at the start of
        // a file `git show` prints, and a revert would then write the file back without it.
        var stdoutTask = ReadStdoutAsync(process);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillAsync(process).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            throw new GitCommandException(string.Join(' ', args), $"didn't finish within {Describe(timeout)}, so Fleet stopped it");
        }

        try
        {
            var stdout = await stdoutTask.WaitAsync(DrainWait, CancellationToken.None).ConfigureAwait(false);
            var stderr = await stderrTask.WaitAsync(DrainWait, CancellationToken.None).ConfigureAwait(false);
            return new GitCommandResult(process.ExitCode, stdout, stderr);
        }
        catch (TimeoutException)
        {
            throw new GitCommandException(string.Join(' ', args), "exited, but something it started kept its output open");
        }
    }

    private static async Task<string> ReadStdoutAsync(Process process)
    {
        using var buffer = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(buffer, CancellationToken.None).ConfigureAwait(false);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static readonly string[] ParsedOutputConfig =
    [
        "log.showSignature=false",
        "diff.suppressBlankEmpty=false",
        "diff.noprefix=false",
        "diff.mnemonicPrefix=false",
        "color.ui=false",
        "color.diff=false",
    ];

    private static ProcessStartInfo StartInfo(string workingDir, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Fleet reads git's output, so the user's config mustn't reshape it: a gpg report in front of every `git log`
        // line (log.showSignature), a blank context line without its space (diff.suppressBlankEmpty) or a/b swapped
        // for c/w (diff.mnemonicPrefix) and none (diff.noprefix) in a patch, escape codes in a diff.
        foreach (var setting in ParsedOutputConfig)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(setting);
        }

        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        // Never wait on a credential prompt nobody can see. Git tries a graphical askpass program before it reads
        // GIT_TERMINAL_PROMPT, so those are switched off too (an empty GIT_ASKPASS also skips core.askPass and
        // SSH_ASKPASS); stored credentials and credential helpers still work.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_ASKPASS"] = "";
        start.Environment["SSH_ASKPASS_REQUIRE"] = "never";
        start.Environment["GCM_INTERACTIVE"] = "never";
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
            start.Environment[key] = value;

        return start;
    }

    /// <summary>Stops git and everything it started, and waits a moment for it to go.</summary>
    private static async Task KillAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            return; // Already exited.
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(KillWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Killed; the system catches up on its own.
        }
    }

    private static string Describe(TimeSpan timeout)
    {
        var (count, unit) = timeout.TotalMinutes >= 1 ? (timeout.TotalMinutes, "minute") : (timeout.TotalSeconds, "second");
        return string.Format(CultureInfo.InvariantCulture, "{0:0.#} {1}{2}", count, unit, count == 1 ? "" : "s");
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
        using var process = new Process();
        process.StartInfo = StartInfo(workingDir, args, environment);

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
            await KillAsync(process);
            throw;
        }

        try
        {
            await stdoutTask.WaitAsync(DrainWait, CancellationToken.None);
            await stderrTask.WaitAsync(DrainWait, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            throw new GitCommandException(string.Join(' ', args), "exited, but something it started kept its output open");
        }

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

    /// <summary>
    /// The branch a <c>git symbolic-ref HEAD</c> answer names (<c>refs/heads/main</c> is <c>main</c>). Asked with
    /// <c>--short</c>, git says <c>heads/main</c> when a tag is also called <c>main</c>.
    /// </summary>
    public static string? BranchNameOf(string? symbolicRef)
    {
        var name = symbolicRef?.Trim();
        const string Heads = "refs/heads/";
        return name is not null && name.StartsWith(Heads, StringComparison.Ordinal) && name.Length > Heads.Length ? name[Heads.Length..] : null;
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
