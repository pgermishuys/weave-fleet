using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.Pi;

/// <summary><see cref="IHarnessRuntime"/> implementation for the Pi AI coding agent.</summary>
public sealed class PiHarnessRuntime : IHarnessRuntime
{
    private static readonly Action<ILogger, string, Exception?> LogSpawned =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, "Spawned"),
            "Pi harness instance spawned: {InstanceId}");

    private static readonly Action<ILogger, string, Exception?> LogSpawnFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(2, "SpawnFailed"),
            "Failed to spawn Pi harness instance: {Reason}");

    private static readonly Action<ILogger, Exception?> LogAvailabilityCheckFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(3, "AvailabilityCheckFailed"),
            "pi binary availability check failed.");

    private readonly FleetOptions _options;
    private readonly ILogger<PiHarnessRuntime> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Initialises the runtime with required dependencies.</summary>
    public PiHarnessRuntime(
        FleetOptions options,
        ILogger<PiHarnessRuntime> logger,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public string HarnessType => "pi";

    /// <inheritdoc />
    public Task<RuntimePreparation> PrepareRuntimeAsync(RuntimePreparationContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var (provider, model) = ResolveProviderModel(context.ModelId);
        var environmentVariables = new Dictionary<string, string>(StringComparer.Ordinal);

        AddCredentialEnvironmentVariables(environmentVariables, context.UserCredentials, provider);

        return Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(
            new PiLaunchArtifacts(provider, model, environmentVariables)));
    }

    /// <inheritdoc />
    public async Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExecutableResolver.Resolve(PiProcessOptions.DefaultBinaryPath),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return new HarnessAvailability(false, "pi binary not found on PATH.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);

            return process.ExitCode == 0
                ? new HarnessAvailability(true, null)
                : new HarnessAvailability(false, $"pi --version exited with code {process.ExitCode}.");
        }
        catch (OperationCanceledException)
        {
            return new HarnessAvailability(false, "Availability check was cancelled.");
        }
        catch (Exception ex)
        {
            LogAvailabilityCheckFailed(_logger, ex);
            return new HarnessAvailability(false, "pi binary not found on PATH.");
        }
    }

    /// <inheritdoc />
    public async Task<IHarnessSession> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
    {
        var instanceId = $"pi-{Guid.NewGuid():N}";
        PiProcessManager? processManager = null;
        PiJsonlClient? client = null;

        try
        {
            HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);
            var artifacts = ResolveArtifacts(options.LaunchArtifacts);

            processManager = new PiProcessManager(_loggerFactory.CreateLogger<PiProcessManager>());
            var connection = await processManager.StartAsync(new PiProcessOptions
            {
                Provider = artifacts.Provider,
                Model = artifacts.Model,
                WorkingDirectory = options.WorkingDirectory,
                EnvironmentVariables = artifacts.EnvironmentVariables,
            }, ct).ConfigureAwait(false);

            client = new PiJsonlClient(connection, _loggerFactory.CreateLogger<PiJsonlClient>());
            var session = new PiHarnessSession(
                instanceId,
                options.SessionId,
                processManager,
                client,
                TimeSpan.FromSeconds(_options.HarnessShutdownTimeoutSeconds),
                _loggerFactory.CreateLogger<PiHarnessSession>());

            var state = await GetStateAsync(client, ct).ConfigureAwait(false);
            if (state is not null)
            {
                session.UpdateState(state);
            }

            if (options.InitialPrompt is not null)
            {
                await session.SendPromptAsync(options.InitialPrompt, null, ct).ConfigureAwait(false);
            }

            LogSpawned(_logger, instanceId, null);
            return session;
        }
        catch
        {
            LogSpawnFailed(_logger, instanceId, null);
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            else if (processManager is not null)
            {
                await processManager.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IHarnessSession> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
    {
        var instanceId = $"pi-{Guid.NewGuid():N}";
        PiProcessManager? processManager = null;
        PiJsonlClient? client = null;

        try
        {
            HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);
            var artifacts = ResolveArtifacts(options.LaunchArtifacts);

            processManager = new PiProcessManager(_loggerFactory.CreateLogger<PiProcessManager>());
            var connection = await processManager.StartAsync(new PiProcessOptions
            {
                Provider = artifacts.Provider,
                Model = artifacts.Model,
                WorkingDirectory = options.WorkingDirectory,
                EnvironmentVariables = artifacts.EnvironmentVariables,
            }, ct).ConfigureAwait(false);

            client = new PiJsonlClient(connection, _loggerFactory.CreateLogger<PiJsonlClient>());
            var session = new PiHarnessSession(
                instanceId,
                options.SessionId,
                processManager,
                client,
                TimeSpan.FromSeconds(_options.HarnessShutdownTimeoutSeconds),
                _loggerFactory.CreateLogger<PiHarnessSession>());

            var initialState = await GetStateAsync(client, ct).ConfigureAwait(false);
            if (initialState is not null)
            {
                session.UpdateState(initialState);
            }

            var switchResponse = await client.SendRequestAsync(new PiSwitchSessionCommand
            {
                Id = NewRequestId(),
                SessionPath = ResolveSessionPath(options.ResumeToken),
            }, ct).ConfigureAwait(false);
            EnsureSuccess(switchResponse);

            var resumedState = await GetStateAsync(client, ct).ConfigureAwait(false);
            if (resumedState is not null)
            {
                session.UpdateState(resumedState);
            }

            LogSpawned(_logger, instanceId, null);
            return session;
        }
        catch
        {
            LogSpawnFailed(_logger, instanceId, null);
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            else if (processManager is not null)
            {
                await processManager.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static PiLaunchArtifacts ResolveArtifacts(RuntimeLaunchArtifacts? artifacts)
    {
        return artifacts as PiLaunchArtifacts
            ?? new PiLaunchArtifacts(
                PiProcessOptions.DefaultProvider,
                PiProcessOptions.DefaultModel,
                new Dictionary<string, string>());
    }

    private static async Task<PiState?> GetStateAsync(PiJsonlClient client, CancellationToken ct)
    {
        var response = await client.SendRequestAsync(new PiGetStateCommand { Id = NewRequestId() }, ct).ConfigureAwait(false);
        EnsureSuccess(response);

        return response.Data is { ValueKind: JsonValueKind.Object } data
            ? JsonSerializer.Deserialize(data, PiJsonContext.Default.PiState)
            : null;
    }

    private static void EnsureSuccess(PiResponseEvent response)
    {
        if (!response.Success)
        {
            throw new InvalidOperationException(response.Error ?? $"Pi command failed: {response.Command}.");
        }
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N");

    private static string ResolveSessionPath(string resumeToken)
    {
        if (string.IsNullOrWhiteSpace(resumeToken))
        {
            return resumeToken;
        }

        try
        {
            using var document = JsonDocument.Parse(resumeToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return resumeToken;
            }

            if (document.RootElement.TryGetProperty("sessionFile", out var sessionFileElement)
                && sessionFileElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(sessionFileElement.GetString()))
            {
                return sessionFileElement.GetString()!;
            }

            if (document.RootElement.TryGetProperty("sessionId", out var sessionIdElement)
                && sessionIdElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(sessionIdElement.GetString()))
            {
                return sessionIdElement.GetString()!;
            }
        }
        catch (JsonException)
        {
            return resumeToken;
        }

        return resumeToken;
    }

    private static (string Provider, string Model) ResolveProviderModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return (PiProcessOptions.DefaultProvider, PiProcessOptions.DefaultModel);
        }

        var separatorIndex = modelId.IndexOf('/', StringComparison.Ordinal);
        return separatorIndex > 0 && separatorIndex < modelId.Length - 1
            ? (modelId[..separatorIndex], modelId[(separatorIndex + 1)..])
            : (PiProcessOptions.DefaultProvider, modelId);
    }

    private static void AddCredentialEnvironmentVariables(
        Dictionary<string, string> environmentVariables,
        IReadOnlyList<UserCredential> credentials,
        string provider)
    {
        var credential = FindCredential(credentials, provider);
        if (credential is null)
        {
            return;
        }

        var environmentVariableName = provider.ToLowerInvariant() switch
        {
            "github-copilot" or "copilot" => "COPILOT_GITHUB_TOKEN",
            "anthropic" => "ANTHROPIC_API_KEY",
            "openai" => "OPENAI_API_KEY",
            _ => null,
        };

        if (environmentVariableName is not null)
        {
            environmentVariables[environmentVariableName] = credential.EncryptedValue;
        }
    }

    /// <inheritdoc />
    public Task<bool> WarmupPooledInstanceAsync(string ownerUserId, CancellationToken ct)
        => Task.FromResult(false);

    private static UserCredential? FindCredential(IReadOnlyList<UserCredential> credentials, string provider)
    {
        return provider.ToLowerInvariant() switch
        {
            "github-copilot" or "copilot" => credentials.FirstOrDefault(static c =>
                string.Equals(c.Namespace, "github", StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Kind, "oauth-access-token", StringComparison.OrdinalIgnoreCase)),
            "anthropic" => credentials.FirstOrDefault(static c =>
                string.Equals(c.Namespace, "anthropic", StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Kind, "api-key", StringComparison.OrdinalIgnoreCase)),
            "openai" => credentials.FirstOrDefault(static c =>
                string.Equals(c.Namespace, "openai", StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Kind, "api-key", StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
    }
}
