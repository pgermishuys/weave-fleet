using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// Finds a harness executable and runs it to check it works. Each runtime's
/// <c>CheckAvailabilityAsync</c> starts here, then adds its own checks (sign-in, for example).
/// </summary>
internal static partial class HarnessProbe
{
    /// <summary>How long a probe (<c>--version</c>, <c>auth status</c>) may run. A hung CLI must not hang the harness list.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>What a harness executable printed and how it exited.</summary>
    internal sealed record ProbeResult(int ExitCode, string StandardOutput, bool TimedOut);

    /// <summary>
    /// Finds <paramref name="command"/> (see <see cref="ExecutableResolver"/>) and runs <c>--version</c>.
    /// Ready with its version and path when it exits 0; otherwise not installed or not working.
    /// </summary>
    /// <param name="displayName">The harness name the user knows, e.g. "OpenCode".</param>
    /// <param name="command">The executable name, or a path the user configured.</param>
    /// <param name="installDirectories">Folders the harness's own installer uses.</param>
    /// <param name="logger">Logs a probe that couldn't start.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static Task<HarnessAvailability> CheckInstalledAsync(
        string displayName,
        string command,
        IEnumerable<string> installDirectories,
        ILogger logger,
        CancellationToken ct)
    {
        var found = ExecutableResolver.TryResolve(command, installDirectories, out var path);
        return CheckInstalledAsync(displayName, command, found ? path : null, logger, ct);
    }

    /// <summary>Test seam: checks an executable already looked up (<see langword="null"/> when it wasn't found).</summary>
    internal static async Task<HarnessAvailability> CheckInstalledAsync(
        string displayName,
        string command,
        string? executablePath,
        ILogger logger,
        CancellationToken ct)
    {
        if (executablePath is null)
        {
            return HarnessAvailability.NotInstalled(IsPath(command)
                ? $"{displayName} isn't installed: there's no file at {command}."
                : $"{displayName} isn't installed: Fleet couldn't find {command} on PATH or in the folders its installer uses.");
        }

        ProbeResult result;
        try
        {
            result = await RunAsync(executablePath, ["--version"], ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            LogProbeFailed(logger, executablePath, ex);
            return HarnessAvailability.NotWorking($"Fleet couldn't run {executablePath}: {ex.Message}", executablePath: executablePath);
        }

        if (result.TimedOut)
        {
            return HarnessAvailability.NotWorking(
                $"{command} --version didn't finish within {Timeout.TotalSeconds:0} seconds.",
                executablePath: executablePath);
        }

        if (result.ExitCode != 0)
        {
            return HarnessAvailability.NotWorking(
                $"{command} --version exited with code {result.ExitCode}.",
                executablePath: executablePath);
        }

        return HarnessAvailability.Ready(ParseVersion(result.StandardOutput), executablePath);
    }

    /// <summary>
    /// Runs <paramref name="executablePath"/> with <paramref name="arguments"/>, reading its output,
    /// and kills it after <see cref="Timeout"/>. Throws when the process can't start.
    /// </summary>
    internal static async Task<ProbeResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Process.Start returned no process for {executablePath}.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        // Drain both streams before waiting, or a chatty CLI blocks on a full pipe.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            return new ProbeResult(process.ExitCode, stdout, TimedOut: false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // already exited
            ct.ThrowIfCancellationRequested();
            return new ProbeResult(-1, string.Empty, TimedOut: true);
        }
    }

    /// <summary>The first version number in <c>--version</c> output: "1.18.30", or "2.1.276" from "2.1.276 (Claude Code)".</summary>
    internal static string? ParseVersion(string output)
    {
        var match = VersionPattern().Match(output);
        return match.Success ? match.Value : null;
    }

    private static bool IsPath(string command) => command.Contains('/') || command.Contains('\\');

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.]+)?")]
    private static partial Regex VersionPattern();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't run harness executable {ExecutablePath}")]
    private static partial void LogProbeFailed(ILogger logger, string executablePath, Exception exception);
}
