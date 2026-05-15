using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// <see cref="IHarnessRuntime"/> implementation for the OpenCode AI coding agent.
/// Handles availability checks, runtime preparation, and spawning/resuming sessions.
/// </summary>
public sealed class OpenCodeHarnessRuntime : IHarnessRuntime
{
    private static readonly Action<ILogger, string, Exception?> LogSpawned =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, "Spawned"),
            "OpenCode harness instance spawned: {InstanceId}");

    private static readonly Action<ILogger, string, Exception?> LogSpawnFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(2, "SpawnFailed"),
            "Failed to spawn OpenCode harness instance: {Reason}");

    private static readonly Action<ILogger, Exception?> LogAvailabilityCheckFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(3, "AvailabilityCheckFailed"),
            "opencode binary availability check failed.");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PortAllocator _portAllocator;
    private readonly FleetOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OpenCodeHarnessRuntime> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IAnalyticsCollector? _analyticsCollector;

    /// <summary>Initialises the runtime with required dependencies.</summary>
    public OpenCodeHarnessRuntime(
        IHttpClientFactory httpClientFactory,
        PortAllocator portAllocator,
        FleetOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<OpenCodeHarnessRuntime> logger,
        ILoggerFactory loggerFactory,
        IAnalyticsCollector? analyticsCollector = null)
    {
        _httpClientFactory = httpClientFactory;
        _portAllocator = portAllocator;
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _analyticsCollector = analyticsCollector;
    }

    /// <inheritdoc />
    public string HarnessType => "opencode";

    /// <inheritdoc />
    public Task<RuntimePreparation> PrepareRuntimeAsync(RuntimePreparationContext context, CancellationToken ct)
    {
        // Step 1: resolve credential requirements for the requested model.
        var requirements = ResolveRequirements(context.ModelId);

        // Step 2: validate that all required credentials are present in the user's credential bag.
        var errors = new List<RuntimePreparationError>();
        var envVars = new Dictionary<string, string>();

        foreach (var requirement in requirements)
        {
            // First-match selection by creation order (credentials are already ordered by created_at ASC).
            var match = context.UserCredentials
                .FirstOrDefault(c =>
                    string.Equals(c.Namespace, requirement.Namespace, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(c.Kind, requirement.Kind, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                errors.Add(new RuntimePreparationError(
                    Code: "MissingCredential",
                    Message: requirement.UserFacingMessage,
                    Guidance: "Add an API key in Settings → Credentials"));
            }
            else
            {
                // Materialise: map the matched credential to its runtime env var name.
                envVars[requirement.EnvironmentVariableName] = match.EncryptedValue;
            }
        }

        if (errors.Count > 0)
            return Task.FromResult<RuntimePreparation>(new RuntimePreparation.NotReady(errors));

        return Task.FromResult<RuntimePreparation>(
            new RuntimePreparation.Ready(new OpenCodeLaunchArtifacts(envVars)));
    }

    /// <summary>
    /// Resolves credential requirements for the given model ID.
    /// Returns an empty list when the model is unknown or null (no requirements → always ready).
    /// </summary>
    private static IReadOnlyList<CredentialRequirement> ResolveRequirements(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
            return [];

        // Model ID format: "<provider>/<model-name>" or just "<provider>"
        var provider = modelId.Contains('/')
            ? modelId[..modelId.IndexOf('/')]
            : modelId;

        return provider.ToLowerInvariant() switch
        {
            "anthropic" => [new CredentialRequirement(
                Namespace: "anthropic",
                Kind: "api-key",
                EnvironmentVariableName: "ANTHROPIC_API_KEY",
                UserFacingMessage: "An Anthropic API key is required to use this model.")],

            "openai" => [new CredentialRequirement(
                Namespace: "openai",
                Kind: "api-key",
                EnvironmentVariableName: "OPENAI_API_KEY",
                UserFacingMessage: "An OpenAI API key is required to use this model.")],

            _ => []
        };
    }

    /// <summary>
    /// Harness-internal credential requirement.
    /// Maps a domain credential (namespace + kind) to its runtime environment variable name.
    /// </summary>
    private sealed record CredentialRequirement(
        string Namespace,
        string Kind,
        string EnvironmentVariableName,
        string UserFacingMessage);

    /// <inheritdoc />
    public async Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExecutableResolver.Resolve("opencode"),
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return new HarnessAvailability(false, "opencode binary not found on PATH.");
            }

            // Drain redirected streams before WaitForExitAsync to prevent deadlock
            // when the OS pipe buffer fills up and the child process blocks on write.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);

            return process.ExitCode == 0
                ? new HarnessAvailability(true, null)
                : new HarnessAvailability(false, $"opencode --version exited with code {process.ExitCode}.");
        }
        catch (OperationCanceledException)
        {
            return new HarnessAvailability(false, "Availability check was cancelled.");
        }
        catch (Exception ex)
        {
            LogAvailabilityCheckFailed(_logger, ex);
            return new HarnessAvailability(false, "opencode binary not found on PATH.");
        }
    }

    /// <inheritdoc />
    public async Task<IHarnessSession> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
    {
        string instanceId = $"opencode-{Guid.NewGuid():N}";
        int allocatedPort = 0;
        OpenCodeProcessManager? processManager = null;

        try
        {
            // 0. Validate the working directory before touching the filesystem
            HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);

            // 1. Allocate port (use 0 for ephemeral — process manager parses actual port from stdout)

            // 2. Generate per-instance credentials
            string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            const string username = "opencode";

            // 3. Start process
            var startupTimeout = TimeSpan.FromSeconds(_options.HarnessStartupTimeoutSeconds);
            processManager = new OpenCodeProcessManager(
                _loggerFactory.CreateLogger<OpenCodeProcessManager>());

            var processInfo = await processManager.StartAsync(
                new OpenCodeProcessOptions
                {
                    Port = 0,
                    Hostname = "127.0.0.1",
                    WorkingDirectory = options.WorkingDirectory,
                    Password = password,
                    Username = username,
                    EnvironmentVariables = options.LaunchArtifacts is OpenCodeLaunchArtifacts spawnArtifacts
                        ? spawnArtifacts.EnvironmentVariables
                        : new Dictionary<string, string>(),
                    StartupTimeout = startupTimeout,
                },
                ct).ConfigureAwait(false);

            allocatedPort = processInfo.Port;

            // 4. Create named HttpClient with base address + Basic Auth
            var httpClient = _httpClientFactory.CreateClient("OpenCode");
            httpClient.BaseAddress = processInfo.BaseUrl;
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes($"{username}:{password}")));

            var openCodeHttpClient = new OpenCodeHttpClient(
                httpClient,
                _loggerFactory.CreateLogger<OpenCodeHttpClient>());

            // 5. Health check with retries
            Exception? lastEx = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await openCodeHttpClient.CheckHealthAsync(ct).ConfigureAwait(false);
                    lastEx = null;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastEx = ex;
                    if (attempt < 2)
                    {
                        await Task.Delay(500, ct).ConfigureAwait(false);
                    }
                }
            }

            if (lastEx is not null)
            {
                throw new InvalidOperationException(
                    $"OpenCode process started but health check failed after 3 attempts.", lastEx);
            }

            // 6. Create instance
            var instance = new OpenCodeHarnessSession(
                instanceId: instanceId,
                fleetSessionId: options.SessionId,
                httpClient: openCodeHttpClient,
                processManager: processManager,
                portAllocator: _portAllocator,
                allocatedPort: allocatedPort,
                workingDirectory: options.WorkingDirectory,
                shutdownTimeout: TimeSpan.FromSeconds(_options.HarnessShutdownTimeoutSeconds),
                scopeFactory: _scopeFactory,
                logger: _loggerFactory.CreateLogger<OpenCodeHarnessSession>(),
                ownerUserId: options.OwnerUserId,
                analyticsCollector: _analyticsCollector,
                projectId: options.ProjectId,
                projectName: options.ProjectName);

            if (options.InitialPrompt is not null)
            {
                await instance.SendPromptAsync(options.InitialPrompt, null, ct).ConfigureAwait(false);
            }

            LogSpawned(_logger, instanceId, null);
            return instance;
        }
        catch
        {
            LogSpawnFailed(_logger, instanceId, null);
            if (processManager is not null)
            {
                await processManager.DisposeAsync().ConfigureAwait(false);
            }
            if (allocatedPort > 0)
            {
                _portAllocator.ReleasePort(allocatedPort);
            }
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IHarnessSession> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
    {
        string instanceId = $"opencode-{Guid.NewGuid():N}";
        int allocatedPort = 0;
        OpenCodeProcessManager? processManager = null;

        try
        {
            HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);

            string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            const string username = "opencode";

            var startupTimeout = TimeSpan.FromSeconds(_options.HarnessStartupTimeoutSeconds);
            processManager = new OpenCodeProcessManager(
                _loggerFactory.CreateLogger<OpenCodeProcessManager>());

            var processInfo = await processManager.StartAsync(
                new OpenCodeProcessOptions
                {
                    Port = 0,
                    Hostname = "127.0.0.1",
                    WorkingDirectory = options.WorkingDirectory,
                    Password = password,
                    Username = username,
                    EnvironmentVariables = options.LaunchArtifacts is OpenCodeLaunchArtifacts resumeArtifacts
                        ? resumeArtifacts.EnvironmentVariables
                        : new Dictionary<string, string>(),
                    StartupTimeout = startupTimeout,
                },
                ct).ConfigureAwait(false);

            allocatedPort = processInfo.Port;

            var httpClient = _httpClientFactory.CreateClient("OpenCode");
            httpClient.BaseAddress = processInfo.BaseUrl;
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes($"{username}:{password}")));

            var openCodeHttpClient = new OpenCodeHttpClient(
                httpClient,
                _loggerFactory.CreateLogger<OpenCodeHttpClient>());

            Exception? lastEx = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await openCodeHttpClient.CheckHealthAsync(ct).ConfigureAwait(false);
                    lastEx = null;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastEx = ex;
                    if (attempt < 2)
                    {
                        await Task.Delay(500, ct).ConfigureAwait(false);
                    }
                }
            }

            if (lastEx is not null)
            {
                throw new InvalidOperationException(
                    $"OpenCode process started but health check failed after 3 attempts.", lastEx);
            }

            var instance = new OpenCodeHarnessSession(
                instanceId: instanceId,
                fleetSessionId: options.SessionId,
                httpClient: openCodeHttpClient,
                processManager: processManager,
                portAllocator: _portAllocator,
                allocatedPort: allocatedPort,
                workingDirectory: options.WorkingDirectory,
                shutdownTimeout: TimeSpan.FromSeconds(_options.HarnessShutdownTimeoutSeconds),
                scopeFactory: _scopeFactory,
                logger: _loggerFactory.CreateLogger<OpenCodeHarnessSession>(),
                ownerUserId: options.OwnerUserId,
                analyticsCollector: _analyticsCollector,
                projectId: options.ProjectId,
                projectName: options.ProjectName,
                openCodeSessionId: options.ResumeToken);

            LogSpawned(_logger, instanceId, null);
            return instance;
        }
        catch
        {
            LogSpawnFailed(_logger, instanceId, null);
            if (processManager is not null)
            {
                await processManager.DisposeAsync().ConfigureAwait(false);
            }
            if (allocatedPort > 0)
            {
                _portAllocator.ReleasePort(allocatedPort);
            }
            throw;
        }
    }
}
