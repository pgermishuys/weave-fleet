using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Wraps a running <c>opencode serve</c> process and HTTP client for a single session.
/// Implements <see cref="IHarnessSession"/>.
/// <para>
/// This class is a pure event producer — it yields all events from the OpenCode SSE
/// stream without filtering or persisting. Durable persistence is handled by
/// <see cref="HarnessEventPersistenceService"/> in <c>HarnessEventRelay.PumpAsync</c>.
/// </para>
/// </summary>
internal sealed partial class OpenCodeHarnessSession : IHarnessSession
{
    private static readonly Action<ILogger, string, Exception?> LogSendPrompt =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1, "SendPrompt"),
            "Sending prompt to OpenCode instance {InstanceId}.");

    private static readonly Action<ILogger, string, Exception?> LogAbort =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(2, "Abort"),
            "Aborting OpenCode instance {InstanceId}.");

    private static readonly Action<ILogger, string, Exception?> LogStop =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(3, "Stop"),
            "Stopping OpenCode instance {InstanceId}.");

    private static readonly Action<ILogger, string, int, Exception?> LogProcessExited =
        LoggerMessage.Define<string, int>(LogLevel.Error, new EventId(4, "ProcessExited"),
            "OpenCode instance {InstanceId} process exited unexpectedly with code {ExitCode}.");

    private static readonly Action<ILogger, string, Exception?> LogSessionCreated =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(5, "SessionCreated"),
            "Created OpenCode session {SessionId}.");

    private static readonly Action<ILogger, string, Exception?> LogPersistFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(6, "PersistFailed"),
            "Failed to persist resume token for session {SessionId}");

    private static readonly Action<ILogger, string, Exception?> LogSendCommand =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(7, "SendCommand"),
            "Sending command to OpenCode instance {InstanceId}.");

    private static readonly Action<ILogger, string, Exception?> LogDelegationFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(8, "DelegationFailed"),
            "Failed to process delegation event for session {SessionId}");

    private readonly OpenCodeHttpClient _httpClient;
    private readonly OpenCodeProcessManager _processManager;
    private readonly PortAllocator _portAllocator;
    private readonly int _allocatedPort;
    private readonly string _workingDirectory;
    private readonly TimeSpan _shutdownTimeout;
    private readonly ILogger<OpenCodeHarnessSession> _logger;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly IAnalyticsCollector? _analyticsCollector;
    private readonly string? _projectId;
    private readonly string? _projectName;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _fleetSessionId;
    private readonly string _ownerUserId;

    private string? _openCodeSessionId;
    private HarnessSessionStatus _status = HarnessSessionStatus.Starting;
    private bool _disposed;

    /// <summary>Initialises the instance with all required dependencies.</summary>
    public OpenCodeHarnessSession(
        string instanceId,
        string fleetSessionId,
        OpenCodeHttpClient httpClient,
        OpenCodeProcessManager processManager,
        PortAllocator portAllocator,
        int allocatedPort,
        string workingDirectory,
        TimeSpan shutdownTimeout,
        IServiceScopeFactory scopeFactory,
        ILogger<OpenCodeHarnessSession> logger,
        string ownerUserId,
        IAnalyticsCollector? analyticsCollector = null,
        string? projectId = null,
        string? projectName = null,
        string? openCodeSessionId = null)
    {
        InstanceId = instanceId;
        _fleetSessionId = fleetSessionId;
        _httpClient = httpClient;
        _processManager = processManager;
        _portAllocator = portAllocator;
        _allocatedPort = allocatedPort;
        _workingDirectory = workingDirectory;
        _shutdownTimeout = shutdownTimeout;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _ownerUserId = ownerUserId;
        _analyticsCollector = analyticsCollector;
        _projectId = projectId;
        _projectName = projectName;
        _openCodeSessionId = openCodeSessionId;

        _status = HarnessSessionStatus.Idle;

        // Subscribe to unexpected process exit
        _processManager.ProcessExited += OnProcessExited;
    }

    /// <inheritdoc />
    public string InstanceId { get; }

    /// <inheritdoc />
    public string HarnessType => "opencode";

    /// <inheritdoc />
    public string? ResumeToken => _openCodeSessionId;

    /// <inheritdoc />
    public HarnessSessionStatus Status => _status;

    // -----------------------------------------------------------------------
    // IHarnessSession
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct)
    {
        await EnsureSessionAsync(ct).ConfigureAwait(false);

        var parts = new List<OpenCodePromptPart>
        {
            new OpenCodePromptTextPart { Text = text },
        };

        if (options?.Attachments is { Count: > 0 } attachments)
        {
            foreach (var attachment in attachments)
            {
                parts.Add(new OpenCodePromptFilePart
                {
                    Mime = attachment.Mime,
                    Url = $"data:{attachment.Mime};base64,{attachment.Data}",
                    Filename = attachment.Filename,
                });
            }
        }

        OpenCodeModelRefRequest? modelRef = null;
        if (options?.ModelId is { } modelId)
        {
            var slash = modelId.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0)
            {
                modelRef = new OpenCodeModelRefRequest
                {
                    ProviderId = modelId[..slash],
                    ModelId = modelId[(slash + 1)..],
                };
            }
            else
            {
                modelRef = new OpenCodeModelRefRequest { ProviderId = modelId, ModelId = modelId };
            }
        }

        var request = new OpenCodePromptRequest
        {
            Parts = parts,
            Agent = options?.Agent,
            Model = modelRef,
        };

        LogSendPrompt(_logger, InstanceId, null);
        await _httpClient.SendPromptAsyncFireAndForget(
            _openCodeSessionId!,
            request,
            _workingDirectory,
            ct).ConfigureAwait(false);

        _status = HarnessSessionStatus.Running;
    }

    /// <inheritdoc />
    public async Task SendCommandAsync(CommandOptions options, CancellationToken ct)
    {
        await EnsureSessionAsync(ct).ConfigureAwait(false);

        // OpenCode's CommandInput expects "model" as a plain string (e.g. "provider/model"),
        // unlike the prompt endpoint which accepts { providerID, modelID }.
        // It also requires "arguments" as a non-optional string.
        var request = new OpenCodeCommandRequest
        {
            Command = options.Command,
            Arguments = options.Arguments ?? string.Empty,
            Agent = options.Agent,
            Model = options.ModelId,
        };

        LogSendCommand(_logger, InstanceId, null);
        await _httpClient.SendCommandAsync(
            _openCodeSessionId!,
            request,
            _workingDirectory,
            ct).ConfigureAwait(false);

        _status = HarnessSessionStatus.Running;
    }

    /// <inheritdoc />
    public async Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct)
    {
        if (_openCodeSessionId is null)
        {
            return new MessagePage([], false);
        }

        var raw = await _httpClient.GetMessagesAsync(
            _openCodeSessionId,
            _workingDirectory,
            query?.Limit,
            query?.Before,
            ct).ConfigureAwait(false);

        var messages = OpenCodeMapper.ToHarnessMessages(raw);

        // OpenCode doesn't return a hasMore flag on this endpoint; use limit as heuristic.
        bool hasMore = query?.Limit.HasValue == true && raw.Count >= query.Limit.Value;

        return new MessagePage(messages, hasMore);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<HarnessEvent> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var sseEvt in _httpClient
            .SubscribeToEventsAsync(_workingDirectory, ct)
            .ConfigureAwait(false))
        {
            var resolvedOpenCodeSessionId = OpenCodeMapper.TryResolveSessionId(sseEvt);
            var isParentEvent = string.IsNullOrWhiteSpace(_openCodeSessionId)
                || string.IsNullOrWhiteSpace(resolvedOpenCodeSessionId)
                || string.Equals(resolvedOpenCodeSessionId, _openCodeSessionId, StringComparison.Ordinal);

            string? routedFleetSessionId = null;
            if (!isParentEvent && !string.IsNullOrWhiteSpace(resolvedOpenCodeSessionId))
            {
                routedFleetSessionId = await TryResolveChildFleetSessionIdAsync(resolvedOpenCodeSessionId).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(routedFleetSessionId))
                    continue;
            }

            // Fire-and-forget analytics intercept — never blocks or throws
            if (isParentEvent && _analyticsCollector is not null)
            {
                var tokenEvent = OpenCodeMapper.TryExtractTokenEvent(
                    sseEvt, _fleetSessionId, _projectId, _projectName, _workingDirectory, _ownerUserId);
                if (tokenEvent is not null)
                    _analyticsCollector.AcceptTokenEvent(tokenEvent);
            }

            var harnessEvent = OpenCodeMapper.ToHarnessEvent(
                sseEvt,
                isParentEvent ? _openCodeSessionId : resolvedOpenCodeSessionId) with
            {
                FleetSessionId = !isParentEvent ? routedFleetSessionId : null
            };

            // Fire-and-forget delegation detection for message.part.updated events.
            // This must remain in the session because it needs access to the raw SSE event
            // and the fleet session context for child session orchestration.
            if (harnessEvent.Type == "message.part.updated")
                _ = TryEmitDelegationAsync(sseEvt);

            yield return harnessEvent;
        }
    }

    private async Task<string?> TryResolveChildFleetSessionIdAsync(string openCodeSessionId)
    {
        using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var session = await repo.GetByHarnessIdAsync(openCodeSessionId).ConfigureAwait(false);
        if (session is null)
            return null;

        if (!string.Equals(session.ParentSessionId, _fleetSessionId, StringComparison.Ordinal))
            return null;

        return session.Id;
    }

    /// <inheritdoc />
    public async Task AbortAsync(CancellationToken ct)
    {
        if (_openCodeSessionId is null) return;

        LogAbort(_logger, InstanceId, null);
        await _httpClient.AbortAsync(_openCodeSessionId, _workingDirectory, ct).ConfigureAwait(false);
        _status = HarnessSessionStatus.Idle;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct)
    {
        if (!_processManager.IsRunning)
        {
            return new HealthCheckResult(false, "Process exited.");
        }

        try
        {
            var health = await _httpClient.CheckHealthAsync(ct).ConfigureAwait(false);
            return new HealthCheckResult(health.Healthy, health.Version is not null ? $"v{health.Version}" : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(false, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct)
    {
        var commands = await _httpClient.GetCommandsAsync(_workingDirectory, ct).ConfigureAwait(false);
        return commands.Select(c => new CommandInfo
        {
            Name = c.Name,
            Description = c.Description,
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct)
    {
        var agents = await _httpClient.GetAgentsAsync(_workingDirectory, ct).ConfigureAwait(false);
        return agents.Select(a => new AgentInfo
        {
            Name = a.Name ?? string.Empty,
            Description = a.Description,
            Mode = a.Mode,
            Hidden = a.Hidden ?? false,
            ModelProviderId = a.Model?.ProviderId,
            ModelId = a.Model?.ModelId,
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct)
    {
        var response = await _httpClient.GetProvidersAsync(_workingDirectory, ct).ConfigureAwait(false);
        // Only return connected providers (ones where credentials are configured).
        var connectedSet = response.Connected?.ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        return response.All
            .Where(p => connectedSet.Contains(p.Id))
            .Select(p => new ProviderInfo
            {
                Id = p.Id,
                Name = p.Name,
                Models = p.Models.Values.Select(m => new ModelInfo
                {
                    Id = m.Id,
                    Name = m.Name,
                }).ToList(),
            }).ToList();
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct)
    {
        _status = HarnessSessionStatus.Stopping;
        LogStop(_logger, InstanceId, null);

        await _processManager.StopAsync(_shutdownTimeout).ConfigureAwait(false);
        _status = HarnessSessionStatus.Stopped;

        if (_allocatedPort > 0)
        {
            _portAllocator.ReleasePort(_allocatedPort);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(CancellationToken ct)
    {
        _status = HarnessSessionStatus.Stopping;
        LogStop(_logger, InstanceId, null);

        if (_openCodeSessionId is not null)
        {
            try
            {
                await _httpClient.DeleteSessionAsync(_openCodeSessionId, _workingDirectory, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex;
            }
        }

        await _processManager.StopAsync(_shutdownTimeout).ConfigureAwait(false);
        _status = HarnessSessionStatus.Stopped;

        if (_allocatedPort > 0)
        {
            _portAllocator.ReleasePort(_allocatedPort);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _processManager.ProcessExited -= OnProcessExited;

        if (_status is not HarnessSessionStatus.Stopped and not HarnessSessionStatus.Error)
        {
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort on dispose
            }
        }

        await _processManager.DisposeAsync().ConfigureAwait(false);
        _sessionLock.Dispose();
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_openCodeSessionId is not null) return;

        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_openCodeSessionId is null)
            {
                var session = await _httpClient.CreateSessionAsync(null, _workingDirectory, ct)
                    .ConfigureAwait(false);
                _openCodeSessionId = session.Id;
                LogSessionCreated(_logger, session.Id, null);
                // Persist resume token for session recovery
                _ = PersistResumeTokenAsync(session.Id);
            }
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private void OnProcessExited(object? sender, int exitCode)
    {
        if (_status is HarnessSessionStatus.Stopping or HarnessSessionStatus.Stopped) return;

        LogProcessExited(_logger, InstanceId, exitCode, null);
        _status = HarnessSessionStatus.Error;
    }

    private async Task PersistResumeTokenAsync(string token)
    {
        try
        {
            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            await repo.UpdateResumeTokenAsync(_fleetSessionId, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPersistFailed(_logger, _fleetSessionId, ex);
        }
    }

    private async Task<bool> TryEmitDelegationAsync(OpenCodeSseEvent sseEvt)
    {
        try
        {
            var extraction = OpenCodeMapper.TryExtractDelegation(sseEvt, _fleetSessionId);
            if (extraction is null)
                return false;

            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            using var scope = _scopeFactory.CreateScope();
            var delegationService = scope.ServiceProvider.GetRequiredService<DelegationService>();

            var delegation = await delegationService.HandleDelegationDetectedAsync(
                extraction.ParentSessionId,
                extraction.ToolCallId,
                extraction.Title).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(extraction.ChildSessionId))
            {
                var sessionOrchestrator = scope.ServiceProvider.GetRequiredService<SessionOrchestrator>();
                var childSessionResult = await sessionOrchestrator.EnsureDelegatedChildSessionAsync(
                    extraction.ParentSessionId,
                    extraction.ChildSessionId,
                    extraction.Title).ConfigureAwait(false);

                if (childSessionResult.IsFailure)
                    return true;

                delegation = await delegationService.HandleChildLinkedAsync(
                        extraction.ParentSessionId,
                        extraction.ToolCallId,
                        childSessionResult.Value.Id)
                    .ConfigureAwait(false)
                    ?? delegation;
            }

            if (extraction.Status is "completed" or "error" or "cancelled")
            {
                await delegationService.HandleDelegationFinishedAsync(
                    delegation.DelegationId,
                    extraction.Status).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex)
        {
            LogDelegationFailed(_logger, _fleetSessionId, ex);
            return false;
        }
    }
}
