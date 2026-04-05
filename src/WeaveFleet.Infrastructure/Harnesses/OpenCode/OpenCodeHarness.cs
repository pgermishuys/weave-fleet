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
/// <see cref="IHarness"/> implementation for the OpenCode AI coding agent.
/// Checks binary availability and spawns <c>opencode serve</c> instances.
/// </summary>
public sealed class OpenCodeHarness : IHarness
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
    private readonly ILogger<OpenCodeHarness> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IAnalyticsCollector? _analyticsCollector;

    /// <summary>Initialises the harness with required dependencies.</summary>
    public OpenCodeHarness(
        IHttpClientFactory httpClientFactory,
        PortAllocator portAllocator,
        FleetOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<OpenCodeHarness> logger,
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
    public string Type => "opencode";

    /// <inheritdoc />
    public string DisplayName => "OpenCode";

    /// <inheritdoc />
    public HarnessCapabilities Capabilities { get; } = new()
    {
        RequiresInitialPrompt = false,
        SupportsAgents = true,
        SupportsModelSelection = true,
        SupportsCommands = true,
        SupportsForking = true,
        SupportsResume = false,
        SupportsImageAttachments = true,
        SupportsStreaming = true,
    };

    /// <inheritdoc />
    public async Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "opencode",
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

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0
                ? new HarnessAvailability(true, null)
                : new HarnessAvailability(false, $"opencode --version exited with code {process.ExitCode}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAvailabilityCheckFailed(_logger, ex);
            return new HarnessAvailability(false, "opencode binary not found on PATH.");
        }
    }

    /// <inheritdoc />
    public async Task<IHarnessInstance> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
    {
        string instanceId = $"opencode-{Guid.NewGuid():N}";
        int allocatedPort = 0;
        OpenCodeProcessManager? processManager = null;

        try
        {
            // 0. Validate the working directory before touching the filesystem
            ValidateWorkingDirectory(options.WorkingDirectory);

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
                    EnvironmentVariables = options.Environment,
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
            var instance = new OpenCodeHarnessInstance(
                instanceId: instanceId,
                fleetSessionId: options.SessionId,
                httpClient: openCodeHttpClient,
                processManager: processManager,
                portAllocator: _portAllocator,
                allocatedPort: allocatedPort,
                workingDirectory: options.WorkingDirectory,
                shutdownTimeout: TimeSpan.FromSeconds(_options.HarnessShutdownTimeoutSeconds),
                scopeFactory: _scopeFactory,
                logger: _loggerFactory.CreateLogger<OpenCodeHarnessInstance>(),
                analyticsCollector: _analyticsCollector,
                projectId: options.ProjectId,
                projectName: options.ProjectName);

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

    private static void ValidateWorkingDirectory(string directory)
        => HarnessHelpers.ValidateWorkingDirectory(directory);
}
