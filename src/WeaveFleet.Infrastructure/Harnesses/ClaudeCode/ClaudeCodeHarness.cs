using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// <see cref="IHarness"/> implementation for the Claude Code AI coding agent.
/// Checks binary availability and spawns claude CLI instances.
/// </summary>
public sealed class ClaudeCodeHarness : IHarness
{
    private static readonly Action<ILogger, string, Exception?> LogSpawned =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, "Spawned"),
            "Claude Code harness instance spawned: {InstanceId}");

    private static readonly Action<ILogger, string, Exception?> LogSpawnFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(2, "SpawnFailed"),
            "Failed to spawn Claude Code harness instance: {Reason}");

    private static readonly Action<ILogger, Exception?> LogAvailabilityCheckFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(3, "AvailabilityCheckFailed"),
            "claude binary availability check failed.");

    private readonly FleetOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ClaudeCodeHarness> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IAnalyticsCollector? _analyticsCollector;

    /// <summary>Initialises the harness with required dependencies.</summary>
    public ClaudeCodeHarness(
        FleetOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<ClaudeCodeHarness> logger,
        ILoggerFactory loggerFactory,
        IAnalyticsCollector? analyticsCollector = null)
    {
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _analyticsCollector = analyticsCollector;
    }

    /// <inheritdoc />
    public string Type => "claude-code";

    /// <inheritdoc />
    public string DisplayName => "Claude Code";

    /// <inheritdoc />
    public HarnessCapabilities Capabilities { get; } = new()
    {
        RequiresInitialPrompt = true,
        SupportsAgents = false,
        SupportsModelSelection = true,
        SupportsCommands = false,
        SupportsForking = false,
        SupportsResume = true,
        SupportsImageAttachments = false,
        SupportsStreaming = true,
        SupportsDelegation = false,
    };

    /// <inheritdoc />
    public async Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
    {
        var binaryPath = _options.ClaudeCode.BinaryPath;

        // 1. Check the binary exists and is runnable
        try
        {
            var versionPsi = new ProcessStartInfo
            {
                FileName = binaryPath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var versionProcess = Process.Start(versionPsi);
            if (versionProcess is null)
            {
                return new HarnessAvailability(false, "claude binary not found on PATH.");
            }

            // Drain redirected streams before WaitForExitAsync to prevent deadlock
            // when the OS pipe buffer fills up and the child process blocks on write.
            var stdoutTask = versionProcess.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = versionProcess.StandardError.ReadToEndAsync(ct);

            await versionProcess.WaitForExitAsync(ct).ConfigureAwait(false);

            await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);

            if (versionProcess.ExitCode != 0)
            {
                return new HarnessAvailability(false,
                    $"claude --version exited with code {versionProcess.ExitCode}.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAvailabilityCheckFailed(_logger, ex);
            return new HarnessAvailability(false, "claude binary not found on PATH.");
        }

        // 2. Check auth status
        try
        {
            var authPsi = new ProcessStartInfo
            {
                FileName = binaryPath,
                Arguments = "auth status",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var authProcess = Process.Start(authPsi);
            if (authProcess is null)
            {
                return new HarnessAvailability(false, "claude auth check failed: could not start process.");
            }

            // Drain redirected streams before WaitForExitAsync to prevent deadlock
            // when the OS pipe buffer fills up and the child process blocks on write.
            var authStdoutTask = authProcess.StandardOutput.ReadToEndAsync(ct);
            var authStderrTask = authProcess.StandardError.ReadToEndAsync(ct);

            await authProcess.WaitForExitAsync(ct).ConfigureAwait(false);

            await authStdoutTask.ConfigureAwait(false);
            await authStderrTask.ConfigureAwait(false);

            if (authProcess.ExitCode != 0)
            {
                return new HarnessAvailability(false, "claude auth not configured.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAvailabilityCheckFailed(_logger, ex);
            return new HarnessAvailability(false, "claude auth check failed.");
        }

        return new HarnessAvailability(true, null);
    }

    /// <inheritdoc />
    public async Task<IHarnessInstance> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
    {
        string instanceId = $"claude-code-{Guid.NewGuid():N}";

        HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);

        var instance = new ClaudeCodeHarnessInstance(
            instanceId: instanceId,
            fleetSessionId: options.SessionId,
            workingDirectory: options.WorkingDirectory,
            config: _options.ClaudeCode,
            environmentVariables: options.Environment,
            shutdownTimeout: TimeSpan.FromSeconds(_options.HarnessShutdownTimeoutSeconds),
            scopeFactory: _scopeFactory,
            logger: _loggerFactory.CreateLogger<ClaudeCodeHarnessInstance>(),
            loggerFactory: _loggerFactory,
            ownerUserId: options.OwnerUserId,
            analyticsCollector: _analyticsCollector,
            projectId: options.ProjectId,
            projectName: options.ProjectName);

        try
        {
            if (options.InitialPrompt is not null)
            {
                await instance.SendPromptAsync(options.InitialPrompt, null, ct).ConfigureAwait(false);
            }

            LogSpawned(_logger, instanceId, null);
            return instance;
        }
        catch
        {
            // Dispose the instance to release SemaphoreSlim, channel, and any process resources
            // before re-throwing to the caller.
            await instance.DisposeAsync().ConfigureAwait(false);
            LogSpawnFailed(_logger, instanceId, null);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<IHarnessInstance> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
    {
        string instanceId = $"claude-code-{Guid.NewGuid():N}";
        HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);

        var instance = new ClaudeCodeHarnessInstance(
            instanceId: instanceId,
            fleetSessionId: options.SessionId,
            workingDirectory: options.WorkingDirectory,
            config: _options.ClaudeCode,
            environmentVariables: options.Environment,
            shutdownTimeout: TimeSpan.FromSeconds(_options.HarnessShutdownTimeoutSeconds),
            scopeFactory: _scopeFactory,
            logger: _loggerFactory.CreateLogger<ClaudeCodeHarnessInstance>(),
            loggerFactory: _loggerFactory,
            ownerUserId: options.OwnerUserId,
            analyticsCollector: _analyticsCollector,
            projectId: options.ProjectId,
            projectName: options.ProjectName,
            claudeSessionId: options.ResumeToken);

        LogSpawned(_logger, instanceId, null);
        return Task.FromResult<IHarnessInstance>(instance);
    }
}
