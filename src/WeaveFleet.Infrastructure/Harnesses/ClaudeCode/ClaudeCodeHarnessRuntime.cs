using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// <see cref="IHarnessRuntime"/> implementation for the Claude Code AI coding agent.
/// Handles availability checks, runtime preparation, and spawning/resuming Claude Code sessions.
/// </summary>
public sealed class ClaudeCodeHarnessRuntime : IHarnessRuntime
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
    private readonly ILogger<ClaudeCodeHarnessRuntime> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IAnalyticsCollector? _analyticsCollector;

    /// <summary>Initialises the runtime with required dependencies.</summary>
    public ClaudeCodeHarnessRuntime(
        FleetOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<ClaudeCodeHarnessRuntime> logger,
        ILoggerFactory loggerFactory,
        IAnalyticsCollector? analyticsCollector = null)
    {
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _analyticsCollector = analyticsCollector;
        _catalog = new ClaudeCodeCatalog(options.ClaudeCode, loggerFactory);
        _skills = new ClaudeCodeFleetSkills(
            Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath)) ?? Environment.CurrentDirectory,
            scopeFactory,
            loggerFactory.CreateLogger<ClaudeCodeFleetSkills>());
    }

    private readonly ClaudeCodeCatalog _catalog;
    private readonly ClaudeCodeFleetSkills _skills;

    /// <summary>The bridge tokens of the claude processes this runtime's sessions run.</summary>
    internal ClaudeCodeBridgeTokenRegistry BridgeTokens { get; } = new();

    /// <inheritdoc />
    /// <remarks>
    /// Claude Code's own model list, with each model's efforts (<see cref="ClaudeCodeCatalog"/>). It has no agents to
    /// choose from.
    /// </remarks>
    public async Task<HarnessCatalog?> GetCatalogAsync(string ownerUserId, string directory, HarnessProfile? profile, CancellationToken ct)
    {
        var skills = await _skills.ForOwnerAsync(ownerUserId).ConfigureAwait(false);
        var providers = await _catalog.GetProvidersAsync(directory, ct, skills).ConfigureAwait(false);
        var defaultModel = _options.ClaudeCode.DefaultModel is { } model
                           && providers.Any(provider => provider.Models.Any(m => string.Equals(m.Id, model, StringComparison.Ordinal)))
            ? model
            : null;
        return new HarnessCatalog
        {
            Agents = [],
            Providers = providers,
            DefaultModelProviderId = defaultModel is null ? null : ClaudeCodeCatalog.ProviderId,
            DefaultModelId = defaultModel,
        };
    }

    /// <summary>Where Fleet listens, for the agent's <c>FLEET_URL</c>; null until it knows.</summary>
    private string? LocalFleetUrl()
    {
        using var scope = _scopeFactory.CreateScope();
        return scope.ServiceProvider.GetService<ILocalFleetUrl>()?.TryGet();
    }

    /// <inheritdoc />
    public string HarnessType => "claude-code";

    /// <inheritdoc />
    public Task<RuntimePreparation> PrepareRuntimeAsync(RuntimePreparationContext context, CancellationToken ct)
        => Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new ClaudeCodeLaunchArtifacts()));

    /// <inheritdoc />
    public async Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
    {
        // Claude Code's installer uses ~/.local/bin, which ExecutableResolver searches for every harness.
        var installed = await HarnessProbe.CheckInstalledAsync(
            "Claude Code", _options.ClaudeCode.BinaryPath, [], _logger, ct).ConfigureAwait(false);
        if (!installed.Available || installed.ExecutablePath is null)
        {
            return installed;
        }

        try
        {
            var auth = await HarnessProbe.RunAsync(installed.ExecutablePath, ["auth", "status"], ct).ConfigureAwait(false);
            if (auth.TimedOut || auth.ExitCode != 0)
            {
                return HarnessAvailability.SignInRequired(
                    "Claude Code isn't signed in. Run claude auth login.",
                    installed.Version,
                    installed.ExecutablePath);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            LogAvailabilityCheckFailed(_logger, ex);
            return HarnessAvailability.NotWorking("claude auth status couldn't run.", installed.Version, installed.ExecutablePath);
        }

        return installed;
    }

    /// <inheritdoc />
    /// <remarks>The native installers from https://code.claude.com/docs/en/setup; both update themselves.</remarks>
    public HarnessSetup GetSetup(HarnessAvailability availability)
    {
        var windows = OperatingSystem.IsWindows();
        // Sign in with the executable Fleet found: the terminal's PATH may not have the installer's folder yet.
        var claude = ShellCommand.Executable(availability.ExecutablePath ?? _options.ClaudeCode.BinaryPath, windows);
        return new HarnessSetup(
            InstallCommand: windows ? "irm https://claude.ai/install.ps1 | iex" : "curl -fsSL https://claude.ai/install.sh | bash",
            SignInCommand: $"{claude} auth login",
            DocsUrl: "https://code.claude.com/docs/en/setup");
    }

    /// <inheritdoc />
    public string LatestVersionPackage => "@anthropic-ai/claude-code";

    /// <inheritdoc />
    /// <remarks>
    /// <c>claude update</c> updates native and npm installs. It leaves winget installs alone ("Claude is up to
    /// date!"), so those are updated with winget.
    /// </remarks>
    public HarnessCommand? GetUpdateCommand(HarnessAvailability availability, string? version)
    {
        if (availability.ExecutablePath is not { } path)
            return null;
        return UpdateCommands.IsWingetInstall(path)
            ? UpdateCommands.Winget("Anthropic.ClaudeCode")
            : UpdateCommands.Native(path, ["update"]);
    }

    /// <inheritdoc />
    public async Task<IHarnessSession> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
    {
        string instanceId = $"claude-code-{Guid.NewGuid():N}";

        HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);

        var instance = new ClaudeCodeHarnessSession(
            instanceId: instanceId,
            fleetSessionId: options.SessionId,
            workingDirectory: options.WorkingDirectory,
            config: _options.ClaudeCode,
            environmentVariables: new Dictionary<string, string>(),
            shutdownTimeout: TimeSpan.FromSeconds(_options.HarnessShutdownTimeoutSeconds),
            scopeFactory: _scopeFactory,
            logger: _loggerFactory.CreateLogger<ClaudeCodeHarnessSession>(),
            loggerFactory: _loggerFactory,
            ownerUserId: options.OwnerUserId,
            analyticsCollector: _analyticsCollector,
            projectId: options.ProjectId,
            projectName: options.ProjectName,
            bridgeTokens: BridgeTokens,
            fleetUrl: LocalFleetUrl,
            catalog: _catalog,
            skills: _skills);

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
    public Task<IHarnessSession> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
    {
        string instanceId = $"claude-code-{Guid.NewGuid():N}";
        HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);

        var instance = new ClaudeCodeHarnessSession(
            instanceId: instanceId,
            fleetSessionId: options.SessionId,
            workingDirectory: options.WorkingDirectory,
            config: _options.ClaudeCode,
            environmentVariables: new Dictionary<string, string>(),
            shutdownTimeout: TimeSpan.FromSeconds(_options.HarnessShutdownTimeoutSeconds),
            scopeFactory: _scopeFactory,
            logger: _loggerFactory.CreateLogger<ClaudeCodeHarnessSession>(),
            loggerFactory: _loggerFactory,
            ownerUserId: options.OwnerUserId,
            analyticsCollector: _analyticsCollector,
            projectId: options.ProjectId,
            projectName: options.ProjectName,
            claudeSessionId: options.ResumeToken,
            // A subagent's child session: what its parent's process saved, which Claude Code can't prompt on its own.
            readOnlyChild: options.DelegatedChild,
            bridgeTokens: BridgeTokens,
            fleetUrl: LocalFleetUrl,
            catalog: _catalog,
            skills: _skills);

        LogSpawned(_logger, instanceId, null);
        return Task.FromResult<IHarnessSession>(instance);
    }

    /// <inheritdoc />
    public Task<bool> WarmupPooledInstanceAsync(string ownerUserId, CancellationToken ct)
        => Task.FromResult(false);
}
