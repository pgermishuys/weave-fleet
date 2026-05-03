using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.AI;
using NuCode.Utilities;

namespace NuCode.Tools;

/// <summary>
/// Executes bash commands in a shell process with output capture, timeout support, and truncation.
/// </summary>
internal sealed class BashTool : INuCodeTool
{
    private const int DefaultTimeoutMs = 120_000;
    private const int MaxOutputLines = 2000;
    private const int MaxOutputBytes = 50 * 1024;

    public string Name => "bash";
    public string Description => "Executes a bash command in a persistent shell session.";

    public AIFunction ToAIFunction() =>
        AIFunctionFactory.Create(ExecuteAsync, new AIFunctionFactoryOptions
        {
            Name = Name,
            Description = Description,
        });

    [Description("Executes a bash command in a persistent shell session.")]
    private static async Task<string> ExecuteAsync(
        [Description("The command to execute")] string command,
        [Description("Clear, concise description of what this command does in 5-10 words")] string description,
        [Description("Optional timeout in milliseconds")] int? timeout = null,
        [Description("The working directory to run the command in. Defaults to the current working directory.")] string? workdir = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "Error: command is required.";
        }

        if (timeout.HasValue && timeout.Value <= 0)
        {
            return "Error: timeout must be a positive number.";
        }

        if (workdir is not null && !Directory.Exists(workdir))
        {
            return $"Error: Working directory does not exist: {workdir}";
        }

        var timeoutMs = timeout ?? DefaultTimeoutMs;
        var workingDir = workdir ?? Directory.GetCurrentDirectory();

        string fileName;
        string arguments;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            fileName = "cmd.exe";
            arguments = $"/c {command}";
        }
        else
        {
            fileName = "/bin/bash";
            arguments = $"-c \"{command.Replace("\"", "\\\"")}\"";
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return $"Error: Failed to start process: {ex.Message}";
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        string stdout;
        string stderr;
        var timedOut = false;

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(cts.Token);

            stdout = stdoutTask.Result;
            stderr = stderrTask.Result;
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Process may have already exited — ignore
            }

            stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
            stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;

            if (!timedOut)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        var combined = new StringBuilder();

        if (!string.IsNullOrEmpty(stdout))
        {
            combined.Append(stdout);
        }

        if (!string.IsNullOrEmpty(stderr))
        {
            if (combined.Length > 0)
            {
                combined.AppendLine();
            }
            combined.Append(stderr);
        }

        var rawOutput = combined.ToString();
        var truncated = OutputTruncator.Truncate(rawOutput, MaxOutputLines, MaxOutputBytes);
        var output = truncated.Content;

        var result = new StringBuilder();

        if (!timedOut && process.HasExited && process.ExitCode != 0)
        {
            result.AppendLine($"Exit code: {process.ExitCode}");
        }

        result.Append(output);

        if (timedOut)
        {
            result.AppendLine();
            result.AppendLine();
            result.AppendLine("<bash_metadata>");
            result.AppendLine($"bash tool terminated command after exceeding timeout {timeoutMs} ms");
            result.Append("</bash_metadata>");
        }

        return result.ToString();
    }
}
