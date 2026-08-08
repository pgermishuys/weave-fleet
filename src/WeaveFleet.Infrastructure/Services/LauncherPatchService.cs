using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Self-heals the launcher script on startup if running in installed layout.
/// </summary>
/// <remarks>
/// <para>
/// The launcher at <c>bin/fleet</c> (or <c>bin/fleet.cmd</c> on Windows) had a bug in early
/// versions where a single <c>case</c> statement failed to parse single-line JSON manifests.
/// This was fixed in commit ed56f404, but existing installs still have the broken launcher
/// in <c>bin/</c> because the self-update mechanism only replaces <c>app/</c> — never <c>bin/</c>.
/// </para>
/// <para>
/// This service runs once at startup and:
/// <list type="number">
/// <item>Checks if running in installed layout (VERSION file exists one level above app dir)</item>
/// <item>Reads the launcher script at <c>../bin/fleet</c> (or <c>fleet.cmd</c>)</item>
/// <item>Checks if it contains the broken pattern (single case with both version and assetFileName)</item>
/// <item>If broken, replaces it with the corrected launcher from <c>app/launchers/</c></item>
/// </list>
/// </para>
/// <para>
/// The service is best-effort: if anything fails, it logs a warning and continues (does not crash the app).
/// </para>
/// </remarks>
public sealed partial class LauncherPatchService : IHostedService
{
    private readonly ILogger<LauncherPatchService> _logger;

    public LauncherPatchService(ILogger<LauncherPatchService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var appDir = AppContext.BaseDirectory;
            var rootDir = Path.GetFullPath(Path.Combine(appDir, ".."));
            var versionFile = Path.Combine(rootDir, "VERSION");

            // Only run in installed layout (where VERSION file exists one level above app dir)
            if (!File.Exists(versionFile))
            {
                LogSkippedNotInstalledLayout(_logger);
                return Task.CompletedTask;
            }

            var isWindows = OperatingSystem.IsWindows();
            var launcherName = isWindows ? "fleet.cmd" : "fleet";
            var launcherPath = Path.Combine(rootDir, "bin", launcherName);

            if (!File.Exists(launcherPath))
            {
                LogSkippedLauncherNotFound(_logger, launcherPath);
                return Task.CompletedTask;
            }

            var launcherContent = File.ReadAllText(launcherPath);

            // Check if the launcher contains the broken pattern.
            // The broken pattern is a single case statement that tries to match both
            // "version" and "assetFileName" patterns in one case...esac block.
            // A simple heuristic: check if the file does NOT contain two separate
            // "case" occurrences within the update parsing section.
            // For sh: look for the broken pattern where both patterns are in one case block
            // For cmd: look for the broken PowerShell pattern
            var isBroken = isWindows
                ? IsWindowsLauncherBroken(launcherContent)
                : IsUnixLauncherBroken(launcherContent);

            if (!isBroken)
            {
                LogSkippedAlreadyPatched(_logger);
                return Task.CompletedTask;
            }

            // Read the corrected launcher from app/launchers/
            var correctedLauncherPath = Path.Combine(appDir, "launchers", launcherName);
            if (!File.Exists(correctedLauncherPath))
            {
                LogPatchFailedCorrectedLauncherNotFound(_logger, correctedLauncherPath);
                return Task.CompletedTask;
            }

            var correctedContent = File.ReadAllText(correctedLauncherPath);

            // Overwrite the broken launcher with the corrected one
            File.WriteAllText(launcherPath, correctedContent);

            // On Unix, ensure the launcher is executable using chmod
            if (!isWindows)
            {
                try
                {
                    var chmodProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = $"+x \"{launcherPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    chmodProcess?.WaitForExit();
                }
                catch
                {
                    // Best-effort: if chmod fails, log and continue
                    // The launcher should still work if it was already executable
                }
            }

            LogPatchApplied(_logger, launcherPath);
        }
        catch (Exception ex)
        {
            // Best-effort: any failure is logged but must not block application boot
            LogPatchFailed(_logger, ex);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool IsUnixLauncherBroken(string content)
    {
        // The broken pattern has both "version" and "assetFileName" in a single case block.
        // The fixed version has two separate case statements (one for each field).
        // Check if the file contains the broken pattern: a single case with both patterns.
        // A simple heuristic: if the file contains only one "case" occurrence in the
        // apply_staged_update function, it's likely broken.
        // More precise: check if there's a case block that contains both patterns.

        // Look for the broken pattern: a case block with both '"version"' and '"assetFileName"'
        // between the same case and esac keywords.
        var lines = content.Split('\n');
        var inApplyUpdate = false;
        var inCaseBlock = false;
        var hasVersionPattern = false;
        var hasAssetFilePattern = false;

        foreach (var line in lines)
        {
            if (line.Contains("apply_staged_update()"))
            {
                inApplyUpdate = true;
                continue;
            }

            if (!inApplyUpdate)
                continue;

            // Exit the function when we hit the next function definition
            if (line.TrimStart().StartsWith('}') || (line.Contains("()") && line.Contains('{')))
            {
                break;
            }

            if (line.Contains("case") && line.Contains("in"))
            {
                inCaseBlock = true;
                hasVersionPattern = false;
                hasAssetFilePattern = false;
                continue;
            }

            if (inCaseBlock)
            {
                if (line.Contains("\"version\""))
                    hasVersionPattern = true;

                if (line.Contains("\"assetFileName\""))
                    hasAssetFilePattern = true;

                if (line.Contains("esac"))
                {
                    // If we found both patterns in the same case block, it's broken
                    if (hasVersionPattern && hasAssetFilePattern)
                        return true;

                    inCaseBlock = false;
                    hasVersionPattern = false;
                    hasAssetFilePattern = false;
                }
            }
        }

        return false;
    }

    private static bool IsWindowsLauncherBroken(string content)
    {
        // The Windows launcher uses PowerShell's ConvertFrom-Json which handles
        // single-line JSON correctly. The Windows launcher was never broken by
        // this bug. However, we still want to update it if it's missing the
        // bin/ replacement logic (added alongside the Unix fix).
        // For now, the Windows launcher is not considered "broken" — it only
        // needs updating via the normal update flow (which now replaces bin/).
        _ = content;
        return false;
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Launcher patch skipped: not running in installed layout (VERSION file not found).")]
    private static partial void LogSkippedNotInstalledLayout(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Launcher patch skipped: launcher not found at {LauncherPath}.")]
    private static partial void LogSkippedLauncherNotFound(ILogger logger, string launcherPath);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Launcher patch skipped: launcher is already patched or does not contain the known broken pattern.")]
    private static partial void LogSkippedAlreadyPatched(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Launcher patch failed: corrected launcher not found at {CorrectedLauncherPath}. " +
                  "The launcher will not be patched. This is expected if running from a development build.")]
    private static partial void LogPatchFailedCorrectedLauncherNotFound(ILogger logger, string correctedLauncherPath);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Launcher patch applied: replaced broken launcher at {LauncherPath} with corrected version.")]
    private static partial void LogPatchApplied(ILogger logger, string launcherPath);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Launcher patch failed (best-effort); application boot continues normally.")]
    private static partial void LogPatchFailed(ILogger logger, Exception ex);
}
