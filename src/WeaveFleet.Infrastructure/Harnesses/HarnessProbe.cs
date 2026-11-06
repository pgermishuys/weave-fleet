using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.IO;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// Finds a harness executable and runs it to check it works. Each runtime's
/// <c>CheckAvailabilityAsync</c> starts here, then adds its own checks (sign-in, for example).
/// </summary>
internal static partial class HarnessProbe
{
    /// <summary>How long a probe (<c>--version</c>, <c>auth status</c>) may run. A hung CLI must not hang the harness list.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The version each executable file last reported, until it changes or fails (<see cref="FileVersion"/>).</summary>
    private static readonly ConcurrentDictionary<(string Path, DateTime WrittenAt), string?> Answered = new();

    /// <summary>What a harness executable printed and how it exited.</summary>
    internal sealed record ProbeResult(int ExitCode, string StandardOutput, bool TimedOut);

    /// <summary>
    /// Finds <paramref name="command"/> (see <see cref="ExecutableResolver"/>) and runs <c>--version</c>.
    /// Ready with its version and path when it exits 0, or when it only ran past the limit and the same file answered
    /// before; otherwise not installed or not working.
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
    /// <param name="timeout">How long <c>--version</c> may run; <see cref="Timeout"/> by default.</param>
    internal static async Task<HarnessAvailability> CheckInstalledAsync(
        string displayName,
        string command,
        string? executablePath,
        ILogger logger,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var limit = timeout ?? Timeout;
        if (executablePath is null)
        {
            return HarnessAvailability.NotInstalled(IsPath(command)
                ? $"{displayName} isn't installed: there's no file at {command}."
                : $"{displayName} isn't installed: Fleet couldn't find {command} on PATH or in the folders its installer uses.");
        }

        var file = FileVersion(executablePath);
        ProbeResult result;
        try
        {
            result = await RunAsync(executablePath, ["--version"], ct, limit).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            LogProbeFailed(logger, executablePath, ex);
            return HarnessAvailability.NotWorking($"Fleet couldn't run {executablePath}: {ex.Message}", executablePath: executablePath);
        }

        if (result.TimedOut)
        {
            // A file that answered before and hasn't changed since is slow, not broken: on a busy machine a cold start
            // can run past the limit (OpenCode's took up to 27 s on a loaded CI runner), and "not working" would hide a
            // harness that starts sessions fine.
            if (file is { } unchanged && Answered.TryGetValue(unchanged, out var version))
            {
                LogProbeSlow(logger, executablePath, limit.TotalSeconds);
                return HarnessAvailability.Ready(version, executablePath);
            }
            return HarnessAvailability.NotWorking(
                $"{command} --version didn't finish within {limit.TotalSeconds:0} seconds.",
                executablePath: executablePath);
        }

        if (result.ExitCode != 0)
        {
            if (file is { } failed)
                Answered.TryRemove(failed, out _);
            return HarnessAvailability.NotWorking(
                $"{command} --version exited with code {result.ExitCode}.",
                executablePath: executablePath);
        }

        var parsed = ParseVersion(result.StandardOutput);
        if (file is { } answered)
            Answered[answered] = parsed;
        return HarnessAvailability.Ready(parsed, executablePath);
    }

    /// <summary>
    /// The file <paramref name="executablePath"/> runs (a link's target) and when it was last written, so an update
    /// reads as a different file; <see langword="null"/> when it can't be read.
    /// </summary>
    private static (string Path, DateTime WrittenAt)? FileVersion(string executablePath)
    {
        try
        {
            var file = new FileInfo(executablePath);
            if (file.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target)
                file = target;
            return file.Exists ? (file.FullName, file.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs <paramref name="executablePath"/> with <paramref name="arguments"/>, reading its output,
    /// and kills it after <paramref name="timeout"/> (<see cref="Timeout"/> by default). Throws when the process can't start.
    /// </summary>
    internal static async Task<ProbeResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, CancellationToken ct, TimeSpan? timeout = null)
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

        using var process = FreshExecutable.Start(psi);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? Timeout);

        // Drain both streams before waiting, or a chatty CLI blocks on a full pipe.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "{ExecutablePath} --version took over {Seconds} s; it answered before and hasn't changed, so it stays ready")]
    private static partial void LogProbeSlow(ILogger logger, string executablePath, double seconds);
}
