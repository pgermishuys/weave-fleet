using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// <see cref="IHarnessRuntime"/> implementation for the OpenCode AI coding agent.
/// Handles availability checks, runtime preparation, and spawning/resuming sessions.
/// </summary>
public sealed class OpenCodeHarnessRuntime : IHarnessRuntime, IDisposable, IAsyncDisposable
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

    private static readonly Action<ILogger, string, Exception?> LogExpireQuestionsFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, "ExpireQuestionsFailed"),
            "Failed to expire pending questions for session {SessionId}.");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PortAllocator _portAllocator;
    private readonly FleetOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OpenCodeHarnessRuntime> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IAnalyticsCollector? _analyticsCollector;
    private readonly OpenCodeFeatureFlagProvider _featureFlagProvider;
    private readonly PoolDemuxBindingTable _poolBindingTable;
    private readonly SseEventDemultiplexer _poolDemultiplexer;
    private readonly PooledOpenCodeInstanceRegistry _pooledInstanceRegistry;
    private readonly ConcurrentDictionary<string, PooledSessionMapping> _pooledSessionMappings = new(StringComparer.Ordinal);
    private long _pooledLeaseGeneration;

    /// <summary>Initialises the runtime with required dependencies.</summary>
    public OpenCodeHarnessRuntime(
        IHttpClientFactory httpClientFactory,
        PortAllocator portAllocator,
        FleetOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<OpenCodeHarnessRuntime> logger,
        ILoggerFactory loggerFactory)
        : this(
            httpClientFactory,
            portAllocator,
            options,
            scopeFactory,
            logger,
            loggerFactory,
            analyticsCollector: null)
    {
    }

    /// <summary>Initialises the runtime with required dependencies.</summary>
    public OpenCodeHarnessRuntime(
        IHttpClientFactory httpClientFactory,
        PortAllocator portAllocator,
        FleetOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<OpenCodeHarnessRuntime> logger,
        ILoggerFactory loggerFactory,
        IAnalyticsCollector? analyticsCollector)
        : this(
            httpClientFactory,
            portAllocator,
            options,
            scopeFactory,
            logger,
            loggerFactory,
            new OpenCodeFeatureFlagProvider(options, scopeFactory),
            analyticsCollector)
    {
    }

    /// <summary>Initialises the runtime with required dependencies.</summary>
    internal OpenCodeHarnessRuntime(
        IHttpClientFactory httpClientFactory,
        PortAllocator portAllocator,
        FleetOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<OpenCodeHarnessRuntime> logger,
        ILoggerFactory loggerFactory,
        OpenCodeFeatureFlagProvider featureFlagProvider,
        IAnalyticsCollector? analyticsCollector)
    {
        _httpClientFactory = httpClientFactory;
        _portAllocator = portAllocator;
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _featureFlagProvider = featureFlagProvider;
        _analyticsCollector = analyticsCollector;
        _poolBindingTable = new PoolDemuxBindingTable();
        _poolDemultiplexer = new SseEventDemultiplexer(
            _poolBindingTable,
            loggerFactory.CreateLogger<SseEventDemultiplexer>());
        _pooledInstanceRegistry = new PooledOpenCodeInstanceRegistry(
            CreatePooledInstanceAsync,
            TimeSpan.FromSeconds(options.Harness.PooledOpenCodeIdleTtlSeconds),
            loggerFactory.CreateLogger<PooledOpenCodeInstanceRegistry>());
    }

    internal OpenCodeHarnessRuntime(
        IHttpClientFactory httpClientFactory,
        PortAllocator portAllocator,
        FleetOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<OpenCodeHarnessRuntime> logger,
        ILoggerFactory loggerFactory,
        OpenCodeFeatureFlagProvider featureFlagProvider,
        IAnalyticsCollector? analyticsCollector,
        Func<string, string, IReadOnlyDictionary<string, string>, CancellationToken, Task<PooledOpenCodeInstance>> pooledInstanceFactory)
    {
        _httpClientFactory = httpClientFactory;
        _portAllocator = portAllocator;
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _featureFlagProvider = featureFlagProvider;
        _analyticsCollector = analyticsCollector;
        _poolBindingTable = new PoolDemuxBindingTable();
        _poolDemultiplexer = new SseEventDemultiplexer(
            _poolBindingTable,
            loggerFactory.CreateLogger<SseEventDemultiplexer>());
        _pooledInstanceRegistry = new PooledOpenCodeInstanceRegistry(
            pooledInstanceFactory,
            TimeSpan.FromSeconds(options.Harness.PooledOpenCodeIdleTtlSeconds),
            loggerFactory.CreateLogger<PooledOpenCodeInstanceRegistry>());
    }

    public async ValueTask DisposeAsync()
    {
        await _poolDemultiplexer.DisposeAsync().ConfigureAwait(false);
        await _pooledInstanceRegistry.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public OpenCodePoolHealthStatus GetPooledOpenCodePoolHealth() => _pooledInstanceRegistry.GetHealthStatus();

    internal PortAllocator PortAllocator => _portAllocator;

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
            new RuntimePreparation.Ready(new OpenCodeLaunchArtifacts(envVars, GetRuntimePreparationModelIds(context.ModelId))));
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
            "anthropic" =>
            [
                new CredentialRequirement(
                    Namespace: "anthropic",
                    Kind: "api-key",
                    EnvironmentVariableName: "ANTHROPIC_API_KEY",
                    UserFacingMessage: "An Anthropic API key is required to use this model.")
            ],

            "openai" =>
            [
                new CredentialRequirement(
                    Namespace: "openai",
                    Kind: "api-key",
                    EnvironmentVariableName: "OPENAI_API_KEY",
                    UserFacingMessage: "An OpenAI API key is required to use this model.")
            ],

            _ => []
        };
    }

    private static IReadOnlyList<string> GetRuntimePreparationModelIds(string? modelId)
    {
        return string.IsNullOrWhiteSpace(modelId) ? [] : [modelId];
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
        var pooledModeEnabled = await IsPooledModeEnabledAsync(options.OwnerUserId, ct).ConfigureAwait(false);
        if (pooledModeEnabled)
        {
            return await SpawnPooledAsync(options, ct).ConfigureAwait(false);
        }

        string instanceId = $"opencode-{Guid.NewGuid():N}";
        int allocatedPort = 0;
        OpenCodeProcessManager? processManager = null;

        try
        {
            // 0. Validate the working directory before touching the filesystem
            HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);

            // 1. Allocate a port only for non-pooled sessions. Pooled instances always use port 0/ephemeral
            // and are tracked by the pool registry rather than the per-session PortAllocator.
            allocatedPort = _portAllocator.AllocatePort();

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
                    Port = allocatedPort,
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
            var instanceHandle = new OwnedInstanceHandle(
                openCodeHttpClient,
                processManager,
                _portAllocator,
                allocatedPort,
                options.WorkingDirectory,
                TimeSpan.FromSeconds(_options.HarnessShutdownTimeoutSeconds));

            var instance = new OpenCodeHarnessSession(
                instanceId: instanceId,
                fleetSessionId: options.SessionId,
                instanceHandle: instanceHandle,
                workingDirectory: options.WorkingDirectory,
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

            processManager = null;
            allocatedPort = 0;
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

    private async Task<IHarnessSession> SpawnPooledAsync(HarnessSpawnOptions options, CancellationToken ct)
    {
        string instanceId = $"opencode-{Guid.NewGuid():N}";

        try
        {
            HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);
            var initialEnvironmentVariables = GetEnvironmentVariables(options.LaunchArtifacts);
            var initialCredentialHash = CredentialHasher.HashEnvironment(initialEnvironmentVariables);
            var initialLaunchPlan = new PooledLaunchPlan(
                options.SessionId,
                options.OwnerUserId,
                options.WorkingDirectory,
                initialEnvironmentVariables,
                initialCredentialHash,
                GetPooledLaunchPlanModelIds(options.LaunchArtifacts));

            var instanceHandle = new LeasedInstanceHandle(
                acquireLeaseAsync: (providerId, modelId, lazyCt) =>
                    AcquirePooledLeaseForLaunchAsync(initialLaunchPlan, providerId, modelId, lazyCt),
                sessionBoundAsync: (openCodeSessionId, pooledInstance, _) =>
                {
                    _pooledSessionMappings[options.SessionId] = new PooledSessionMapping(
                        options.SessionId,
                        options.OwnerUserId,
                        pooledInstance.Key,
                        openCodeSessionId,
                        options.WorkingDirectory,
                        pooledInstance.Key);
                    return Task.CompletedTask;
                },
                _poolDemultiplexer,
                _poolBindingTable,
                options.WorkingDirectory,
                options.SessionId,
                options.OwnerUserId,
                Guid.NewGuid());

            var instance = new OpenCodeHarnessSession(
                instanceId: instanceId,
                fleetSessionId: options.SessionId,
                instanceHandle: instanceHandle,
                workingDirectory: options.WorkingDirectory,
                scopeFactory: _scopeFactory,
                logger: _loggerFactory.CreateLogger<OpenCodeHarnessSession>(),
                ownerUserId: options.OwnerUserId,
                analyticsCollector: _analyticsCollector,
                projectId: options.ProjectId,
                projectName: options.ProjectName,
                openCodeSessionId: null,
                initialStatus: HarnessSessionStatus.Starting);

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
            throw;
        }
    }

    private sealed record PooledSessionMapping(
        string FleetSessionId,
        string OwnerUserId,
        string PooledInstanceKey,
        string OpenCodeSessionId,
        string Directory,
        string CredentialHash);

    private sealed record PooledLaunchPlan(
        string FleetSessionId,
        string OwnerUserId,
        string WorkingDirectory,
        IReadOnlyDictionary<string, string> EnvironmentVariables,
        string CredentialHash,
        IReadOnlyList<string> ModelIds);

    private sealed record PooledCredentialPlan(
        IReadOnlyDictionary<string, string> EnvironmentVariables,
        string CredentialHash);

    private async Task<(InstanceLease Lease, long LeaseGeneration)> AcquirePooledLeaseForLaunchAsync(
        PooledLaunchPlan launchPlan,
        string? providerId,
        string? modelId,
        CancellationToken ct)
    {
        var credentialPlan = await ResolveFreshPooledCredentialPlanAsync(launchPlan, providerId, modelId, ct)
            .ConfigureAwait(false);
        var lease = await _pooledInstanceRegistry
            .AcquireAsync(credentialPlan.CredentialHash, credentialPlan.EnvironmentVariables, launchPlan.WorkingDirectory, ct)
            .ConfigureAwait(false);
        var leaseGeneration = Interlocked.Increment(ref _pooledLeaseGeneration);

        return (lease, leaseGeneration);
    }

    private async Task<PooledCredentialPlan> ResolveFreshPooledCredentialPlanAsync(
        PooledLaunchPlan launchPlan,
        string? providerId,
        string? modelId,
        CancellationToken ct)
    {
        var resolvedModelId = ResolveCredentialModelId(providerId, modelId);
        if (string.IsNullOrWhiteSpace(resolvedModelId))
        {
            if (launchPlan.ModelIds.Count > 0)
            {
                return await ResolveFreshPooledCredentialPlanAsync(launchPlan, launchPlan.ModelIds, ct)
                    .ConfigureAwait(false);
            }

            return new PooledCredentialPlan(launchPlan.EnvironmentVariables, launchPlan.CredentialHash);
        }

        return await ResolveFreshPooledCredentialPlanAsync(launchPlan, [resolvedModelId], ct).ConfigureAwait(false);
    }

    private async Task<PooledCredentialPlan> ResolveFreshPooledCredentialPlanAsync(
        PooledLaunchPlan launchPlan,
        IReadOnlyList<string> modelIds,
        CancellationToken ct)
    {
        if (modelIds.Count == 0)
        {
            return new PooledCredentialPlan(launchPlan.EnvironmentVariables, launchPlan.CredentialHash);
        }

        using var userScope = BackgroundUserContext.BeginScope(launchPlan.OwnerUserId);
        using var scope = _scopeFactory.CreateScope();
        var credentialStore = scope.ServiceProvider.GetService<ICredentialStore>();
        if (credentialStore is null)
        {
            return new PooledCredentialPlan(launchPlan.EnvironmentVariables, launchPlan.CredentialHash);
        }

        var credentials = await credentialStore.GetDecryptedCredentialsAsync(launchPlan.OwnerUserId).ConfigureAwait(false);
        var environmentVariables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var modelId in modelIds)
        {
            var preparation = await PrepareRuntimeAsync(new RuntimePreparationContext
            {
                UserId = launchPlan.OwnerUserId,
                UserCredentials = credentials,
                ModelId = modelId,
                WorkingDirectory = launchPlan.WorkingDirectory,
            }, ct).ConfigureAwait(false);

            if (preparation is RuntimePreparation.NotReady notReady)
            {
                var message = string.Join(" ", notReady.Errors.Select(error => error.Message));
                throw new InvalidOperationException(message);
            }

            var freshEnvironmentVariables = GetEnvironmentVariables(((RuntimePreparation.Ready)preparation).Artifacts);
            foreach (var environmentVariable in freshEnvironmentVariables)
            {
                environmentVariables[environmentVariable.Key] = environmentVariable.Value;
            }
        }

        return new PooledCredentialPlan(environmentVariables, CredentialHasher.HashEnvironment(environmentVariables));
    }

    private static IReadOnlyList<string> GetPooledLaunchPlanModelIds(RuntimeLaunchArtifacts? launchArtifacts)
    {
        return launchArtifacts is OpenCodeLaunchArtifacts artifacts ? artifacts.ModelIds : [];
    }

    private static string? ResolveCredentialModelId(string? providerId, string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId))
            return modelId.Contains('/', StringComparison.Ordinal) || string.IsNullOrWhiteSpace(providerId)
                ? modelId
                : $"{providerId}/{modelId}";

        return providerId;
    }

    private async Task<PooledOpenCodeInstance> CreatePooledInstanceAsync(
        string credentialHash,
        string directory,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken ct)
    {
        var instanceId = $"opencode-pool-{Guid.NewGuid():N}";
        OpenCodeProcessManager? processManager = null;

        try
        {
            var startupTimeout = TimeSpan.FromSeconds(_options.HarnessStartupTimeoutSeconds);
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            const string username = "opencode";

            processManager = new OpenCodeProcessManager(
                _loggerFactory.CreateLogger<OpenCodeProcessManager>());

            var processInfo = await processManager.StartAsync(
                new OpenCodeProcessOptions
                {
                    Port = 0,
                    Hostname = "127.0.0.1",
                    WorkingDirectory = directory,
                    Password = password,
                    Username = username,
                    EnvironmentVariables = environmentVariables,
                    StartupTimeout = startupTimeout,
                },
                ct).ConfigureAwait(false);

            var httpClient = _httpClientFactory.CreateClient("OpenCode");
            httpClient.BaseAddress = processInfo.BaseUrl;
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

            var openCodeHttpClient = new OpenCodeHttpClient(
                httpClient,
                _loggerFactory.CreateLogger<OpenCodeHttpClient>());

            await CheckOpenCodeHealthAsync(openCodeHttpClient, ct).ConfigureAwait(false);

            var ownedProcessManager = processManager;
            processManager = null;

            return new PooledOpenCodeInstance(
                credentialHash,
                instanceId,
                processInfo.ProcessId,
                openCodeHttpClient,
                ownedProcessManager,
                async () => await ownedProcessManager.DisposeAsync().ConfigureAwait(false));
        }
        catch
        {
            if (processManager is not null)
            {
                await processManager.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static IReadOnlyDictionary<string, string> GetEnvironmentVariables(RuntimeLaunchArtifacts? launchArtifacts)
    {
        return launchArtifacts is OpenCodeLaunchArtifacts artifacts
            ? artifacts.EnvironmentVariables
            : new Dictionary<string, string>();
    }

    private static async Task CheckOpenCodeHealthAsync(OpenCodeHttpClient openCodeHttpClient, CancellationToken ct)
    {
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
                "OpenCode process started but health check failed after 3 attempts.", lastEx);
        }
    }

    /// <inheritdoc />
    public async Task<IHarnessSession> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
    {
        var pooledModeEnabled = await IsPooledModeEnabledAsync(options.OwnerUserId, ct).ConfigureAwait(false);
        if (pooledModeEnabled)
        {
            return await ResumePooledAsync(options, ct).ConfigureAwait(false);
        }

        string instanceId = $"opencode-{Guid.NewGuid():N}";
        int allocatedPort = 0;
        OpenCodeProcessManager? processManager = null;

        try
        {
            HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);

            allocatedPort = _portAllocator.AllocatePort();

            string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            const string username = "opencode";

            var startupTimeout = TimeSpan.FromSeconds(_options.HarnessStartupTimeoutSeconds);
            processManager = new OpenCodeProcessManager(
                _loggerFactory.CreateLogger<OpenCodeProcessManager>());

            var processInfo = await processManager.StartAsync(
                new OpenCodeProcessOptions
                {
                    Port = allocatedPort,
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

            var instanceHandle = new OwnedInstanceHandle(
                openCodeHttpClient,
                processManager,
                _portAllocator,
                allocatedPort,
                options.WorkingDirectory,
                TimeSpan.FromSeconds(_options.HarnessShutdownTimeoutSeconds));

            var instance = new OpenCodeHarnessSession(
                instanceId: instanceId,
                fleetSessionId: options.SessionId,
                instanceHandle: instanceHandle,
                workingDirectory: options.WorkingDirectory,
                scopeFactory: _scopeFactory,
                logger: _loggerFactory.CreateLogger<OpenCodeHarnessSession>(),
                ownerUserId: options.OwnerUserId,
                analyticsCollector: _analyticsCollector,
                projectId: options.ProjectId,
                projectName: options.ProjectName,
                openCodeSessionId: options.ResumeToken);

            // Expire any pending question tool parts from the previous harness lifetime.
            // When an OpenCode session is resumed, the previous process is gone and any
            // unanswered questions are no longer valid.
            await ExpirePendingQuestionsAsync(options.SessionId, options.OwnerUserId, ct).ConfigureAwait(false);

            processManager = null;
            allocatedPort = 0;
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

    private async Task<IHarnessSession> ResumePooledAsync(HarnessResumeOptions options, CancellationToken ct)
    {
        HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);

        var environmentVariables = GetEnvironmentVariables(options.LaunchArtifacts);
        var currentCredentialHash = CredentialHasher.HashEnvironment(environmentVariables);

        var mapping = ResolvePooledSessionMapping(options, currentCredentialHash);
        var credentialHash = mapping.CredentialHash;
        var directory = mapping.Directory;
        var openCodeSessionId = mapping.OpenCodeSessionId;

        return await ResumePooledWithLeaseAsync(
            options,
            credentialHash,
            directory,
            openCodeSessionId,
            environmentVariables,
            allowCrashRetry: true,
            ct).ConfigureAwait(false);
    }

    private PooledSessionMapping ResolvePooledSessionMapping(HarnessResumeOptions options, string currentCredentialHash)
    {
        if (_pooledSessionMappings.TryGetValue(options.SessionId, out var mapping))
        {
            if (!string.Equals(mapping.OwnerUserId, options.OwnerUserId, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("The pooled OpenCode session belongs to a different user.");
            }

            return mapping;
        }

        // Safe persisted-token fallback after process/app restart: scope by the current user's
        // launch artifacts and working directory. There is no in-memory ownership mapping to trust,
        // so never reuse another user's existing mapping.
        return new PooledSessionMapping(
            options.SessionId,
            options.OwnerUserId,
            currentCredentialHash,
            options.ResumeToken,
            options.WorkingDirectory,
            currentCredentialHash);
    }

    private async Task<IHarnessSession> ResumePooledWithLeaseAsync(
        HarnessResumeOptions options,
        string credentialHash,
        string directory,
        string openCodeSessionId,
        IReadOnlyDictionary<string, string> environmentVariables,
        bool allowCrashRetry,
        CancellationToken ct)
    {
        string instanceId = $"opencode-{Guid.NewGuid():N}";
        LeasedInstanceHandle? instanceHandle = null;
        InstanceLease? leaseToRelease = null;

        try
        {
            leaseToRelease = await _pooledInstanceRegistry
                .AcquireAsync(credentialHash, environmentVariables, directory, ct)
                .ConfigureAwait(false);

            var openCodeHttpClient = leaseToRelease.Instance.HttpClient
                ?? throw new InvalidOperationException("Pooled OpenCode instance does not expose an HTTP client.");

            var resolvedOpenCodeSessionId = await ResolveOpenCodeSessionIdAsync(
                openCodeHttpClient,
                options.SessionId,
                options.OwnerUserId,
                credentialHash,
                directory,
                openCodeSessionId,
                ct).ConfigureAwait(false);

            instanceHandle = new LeasedInstanceHandle(
                leaseToRelease,
                _poolDemultiplexer,
                _poolBindingTable,
                directory,
                options.SessionId,
                options.OwnerUserId,
                Guid.NewGuid(),
                Interlocked.Increment(ref _pooledLeaseGeneration));

            var instance = new OpenCodeHarnessSession(
                instanceId: instanceId,
                fleetSessionId: options.SessionId,
                instanceHandle: instanceHandle,
                workingDirectory: directory,
                scopeFactory: _scopeFactory,
                logger: _loggerFactory.CreateLogger<OpenCodeHarnessSession>(),
                ownerUserId: options.OwnerUserId,
                analyticsCollector: _analyticsCollector,
                projectId: options.ProjectId,
                projectName: options.ProjectName,
                openCodeSessionId: resolvedOpenCodeSessionId);

            leaseToRelease = null;
            instanceHandle = null;

            await ExpirePendingQuestionsAsync(options.SessionId, options.OwnerUserId, ct).ConfigureAwait(false);

            LogSpawned(_logger, instanceId, null);
            return instance;
        }
        catch (HttpRequestException ex) when (allowCrashRetry && ShouldRetryPooledResumeAfterCrash(ex))
        {
            if (leaseToRelease is not null)
            {
                await leaseToRelease.Instance.ReportCrashAsync(ex).ConfigureAwait(false);
            }

            if (instanceHandle is not null)
            {
                await instanceHandle.DisposeAsync().ConfigureAwait(false);
            }
            else if (leaseToRelease is not null)
            {
                await leaseToRelease.DisposeAsync().ConfigureAwait(false);
            }

            return await ResumePooledWithLeaseAsync(
                options,
                credentialHash,
                directory,
                openCodeSessionId,
                environmentVariables,
                allowCrashRetry: false,
                ct).ConfigureAwait(false);
        }
        catch
        {
            LogSpawnFailed(_logger, instanceId, null);
            if (instanceHandle is not null)
            {
                await instanceHandle.DisposeAsync().ConfigureAwait(false);
            }
            else if (leaseToRelease is not null)
            {
                await leaseToRelease.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task<string> ResolveOpenCodeSessionIdAsync(
        OpenCodeHttpClient openCodeHttpClient,
        string fleetSessionId,
        string ownerUserId,
        string credentialHash,
        string directory,
        string? openCodeSessionId,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(openCodeSessionId))
        {
            var existingSessionFound = await TryGetOpenCodeSessionAsync(
                openCodeHttpClient,
                openCodeSessionId,
                directory,
                ct).ConfigureAwait(false);

            if (existingSessionFound)
            {
                return openCodeSessionId;
            }
        }

        // The OpenCode process is alive but lost the session (for example after a crash/restart).
        // Create a fresh OpenCode session and update the Fleet resume token. Historical message
        // replay/import is a higher-level concern; this keeps future resumes pointed at the live OC session.
        var newSession = await openCodeHttpClient
            .CreateSessionAsync(null, directory, ct)
            .ConfigureAwait(false);

        _pooledSessionMappings[fleetSessionId] = new PooledSessionMapping(
            fleetSessionId,
            ownerUserId,
            credentialHash,
            newSession.Id,
            directory,
            credentialHash);

        await PersistResumeTokenAsync(fleetSessionId, ownerUserId, newSession.Id).ConfigureAwait(false);
        return newSession.Id;
    }

    private static async Task<bool> TryGetOpenCodeSessionAsync(
        OpenCodeHttpClient openCodeHttpClient,
        string openCodeSessionId,
        string directory,
        CancellationToken ct)
    {
        try
        {
            await openCodeHttpClient.GetSessionAsync(openCodeSessionId, directory, ct).ConfigureAwait(false);
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static bool ShouldRetryPooledResumeAfterCrash(HttpRequestException exception)
    {
        return exception.StatusCode is null or >= HttpStatusCode.InternalServerError;
    }

    private async Task PersistResumeTokenAsync(string fleetSessionId, string ownerUserId, string resumeToken)
    {
        try
        {
            using var userScope = BackgroundUserContext.BeginScope(ownerUserId);
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            await repo.UpdateResumeTokenAsync(fleetSessionId, resumeToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogExpireQuestionsFailed(_logger, fleetSessionId, ex);
        }
    }

    internal Task<bool> IsPooledModeEnabledAsync(string ownerUserId, CancellationToken ct) =>
        _featureFlagProvider.IsPooledOpenCodeHarnessEnabledAsync(ownerUserId, ct);

    /// <summary>
    /// Expires any pending/running question tool parts for the given session.
    /// Called during resume to ensure stale questions are marked as errors
    /// before the new harness instance starts streaming events.
    /// Broadcasts <c>message.part.updated</c> so already-subscribed clients
    /// (v2 WebSocket) see the state change without requiring a page refresh.
    /// </summary>
    private async Task ExpirePendingQuestionsAsync(string fleetSessionId, string ownerUserId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            using var userScope = BackgroundUserContext.BeginScope(ownerUserId);
            var messageRepository = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            var broadcaster = scope.ServiceProvider.GetRequiredService<IEventBroadcaster>();

            var messages = await messageRepository.GetBySessionAsync(fleetSessionId, limit: 200, beforeMessageId: null)
                .ConfigureAwait(false);

            foreach (var message in messages)
            {
                var parts = JsonSerializer.Deserialize(message.PartsJson,
                    ApplicationJsonContext.Default.ListMessagePart);
                if (parts is null) continue;

                var modified = false;
                for (var i = 0; i < parts.Count; i++)
                {
                    if (parts[i] is ToolUsePart
                        {
                            ToolName: "question", State: ToolUseState.Pending or ToolUseState.Running
                        } tool)
                    {
                        parts[i] = tool with { State = ToolUseState.Error };
                        modified = true;

                        // Broadcast using toolCallId as the part ID — this matches
                        // the v1 client which sets partId = toolCallId in convertFleetMessageToAccumulated.
                        var partPayload = new MessagePartUpdatedPayload
                        {
                            SessionId = fleetSessionId,
                            Part = new ToolMessageEventPart
                            {
                                Id = tool.ToolCallId,
                                SessionId = fleetSessionId,
                                MessageId = message.Id,
                                ToolName = tool.ToolName,
                                CallId = tool.ToolCallId,
                                State = new ToolErrorState { Input = tool.Arguments },
                            },
                        };

                        var payload = JsonSerializer.SerializeToElement(partPayload,
                            InfrastructureJsonContext.Default.MessagePartUpdatedPayload);
                        await broadcaster.BroadcastAsync(
                            $"session:{fleetSessionId}",
                            EventTypes.MessagePartUpdated,
                            payload,
                            ownerUserId,
                            ct).ConfigureAwait(false);
                    }
                }

                if (modified)
                {
                    var updated = new PersistedMessage
                    {
                        Id = message.Id,
                        SessionId = message.SessionId,
                        Role = message.Role,
                        PartsJson = JsonSerializer.Serialize(parts, ApplicationJsonContext.Default.ListMessagePart),
                        Timestamp = message.Timestamp,
                        CreatedAt = message.CreatedAt,
                        AgentName = message.AgentName,
                        ModelId = message.ModelId,
                    };
                    await messageRepository.UpsertAsync(updated).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            LogExpireQuestionsFailed(_logger, fleetSessionId, ex);
        }
    }
}
