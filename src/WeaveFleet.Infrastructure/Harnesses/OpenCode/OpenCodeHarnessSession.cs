using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Wraps a running <c>opencode serve</c> process and HTTP client for a single session.
/// Implements <see cref="IHarnessSession"/>.
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
            "Failed to persist message event for session {SessionId}");

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
    private readonly ConcurrentDictionary<(string MessageKey, string PartId), string> _bufferedTextDeltas = new();

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

            var targetFleetSessionId = routedFleetSessionId ?? _fleetSessionId;

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

            BufferTextDelta(targetFleetSessionId, harnessEvent);

            var handledDurably = await TryHandleDurableEventAsync(targetFleetSessionId, harnessEvent, sseEvt).ConfigureAwait(false);
            if (handledDurably)
                continue;

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

    private async Task<bool> TryHandleDurableEventAsync(string fleetSessionId, HarnessEvent evt, OpenCodeSseEvent sseEvt)
    {
        // Durable vs ephemeral event policy for OpenCode session activity:
        //
        // Persisted via DB + transactional outbox:
        // - message.created
        //   Authoritative creation/update envelope for a user/assistant message.
        //   Stored because it contributes to durable conversation history and must
        //   survive reload/reconnect.
        // - message.updated
        //   Authoritative snapshot update for a message. Stored for the same reason:
        //   it changes committed session state and must be replayable.
        // - message.part.updated
        //   Authoritative update for a concrete message part. Stored because this is
        //   the committed content snapshot that the client should recover after a
        //   reload/reconnect. This can legitimately represent a partial committed
        //   message while generation is in flight.
        //
        // Not persisted here (ephemeral/live-only elsewhere):
        // - message.part.delta
        //   Streaming preview chunks for in-progress generation. These improve live
        //   UX but are not treated as committed state. If the process dies before a
        //   corresponding *.updated event arrives, the preview can be lost and the
        //   client falls back to the last committed snapshot.
        // - session.status / session.idle / error / permission.*
        //   Transport/control-plane signals rather than durable conversation history.
        //
        // Keep this classifier aligned with HarnessEventRelay.IsEphemeralRelayEvent so
        // every event type has exactly one path: durable via outbox, or ephemeral via
        // direct broadcast.
        if (evt.Type is "message.created" or "message.updated")
            return await TryPersistMessageAsync(fleetSessionId, evt).ConfigureAwait(false);

        if (evt.Type == "message.part.updated")
        {
            var handledDelegation = await TryEmitDelegationAsync(sseEvt).ConfigureAwait(false);
            var handledMessagePart = await TryPersistPartAsync(fleetSessionId, evt).ConfigureAwait(false);
            return handledDelegation || handledMessagePart;
        }

        return false;
    }

    private async Task<bool> TryPersistMessageAsync(string fleetSessionId, HarnessEvent evt)
    {
        // Only process message lifecycle events that carry authoritative message metadata.
        if (evt.Type is not ("message.created" or "message.updated"))
            return false;

        try
        {
            if (!evt.Payload.HasValue || evt.Payload.Value.ValueKind != JsonValueKind.Object)
                return false;

            var payload = evt.Payload.Value;

            // Guard: must have an "info" property
            if (!payload.TryGetProperty("info", out var infoEl))
                return false;

            // Guard: only persist user and assistant messages.
            // Read role from raw JSON — avoid polymorphic deserialization of the abstract base type,
            // which requires the discriminator to be the first property in STJ polymorphism.
            if (!infoEl.TryGetProperty("role", out var roleEl))
                return false;

            var role = roleEl.GetString();
            if (role is not ("user" or "assistant"))
                return false;

            // Deserialize the concrete info type directly (avoids STJ polymorphism ordering issues).
            OpenCodeMessageInfo? info = role == "assistant"
                ? infoEl.Deserialize<OpenCodeAssistantMessage>(OpenCodeJsonOptions.Default)
                : infoEl.Deserialize<OpenCodeUserMessage>(OpenCodeJsonOptions.Default);
            if (info is null) return false;

            // Deserialize parts array manually — same STJ polymorphism workaround as
            // DeserializeParts: the "type" discriminator may not be the first property.
            IReadOnlyList<OpenCodeMessagePart> parts = [];
            if (payload.TryGetProperty("parts", out var partsEl))
                parts = OpenCodeHttpClient.DeserializeParts(partsEl);

            var openCodeMessage = new OpenCodeMessageWithParts { Info = info, Parts = parts };
            var harnessMessage = OpenCodeMapper.ToHarnessMessage(openCodeMessage);

            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            using var scope = _scopeFactory.CreateScope();
            var messageRepo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            var sessionActivityWriteService = scope.ServiceProvider.GetRequiredService<SessionActivityWriteService>();

            var persisted = MessagePersistenceService.ToPersistedMessage(fleetSessionId, harnessMessage);
            var existing = await messageRepo.GetByIdAsync(persisted.Id, persisted.SessionId).ConfigureAwait(false);

            // Don't overwrite existing message parts with empty lifecycle skeletons.
            if (harnessMessage.Parts.Count == 0)
            {
                if (existing is not null)
                {
                    var merged = MessagePersistenceService.MergeMetadata(
                        existing,
                        persisted.Role,
                        persisted.AgentName);

                    if (existing.Role != merged.Role
                        || existing.AgentName != merged.AgentName)
                    {
                        await WriteDurableEventAsync(fleetSessionId, sessionActivityWriteService, evt, merged).ConfigureAwait(false);
                        return true;
                    }

                    return false;
                }
            }

            if (evt.Type == "message.updated" && existing is not null)
            {
                var merged = ApplyBufferedTextDeltaIfPresent(
                    fleetSessionId,
                    existing,
                    persisted.Id,
                    persisted.Role,
                    persisted.AgentName);

                if (existing.Role != merged.Role
                    || existing.AgentName != merged.AgentName
                    || existing.PartsJson != merged.PartsJson)
                {
                    await WriteDurableEventAsync(fleetSessionId, sessionActivityWriteService, evt, merged).ConfigureAwait(false);
                    return true;
                }

                return false;
            }

            await WriteDurableEventAsync(fleetSessionId, sessionActivityWriteService, evt, persisted).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // Silent failure — persistence must never crash event stream
            LogPersistFailed(_logger, _fleetSessionId, ex);
            return false;
        }
    }

    private async Task<bool> TryPersistPartAsync(string fleetSessionId, HarnessEvent evt)
    {
        // Only process message.part.updated events
        if (evt.Type is not "message.part.updated")
            return false;

        try
        {
            if (!evt.Payload.HasValue || evt.Payload.Value.ValueKind != JsonValueKind.Object)
                return false;

            var payload = evt.Payload.Value;

            // Extract the "part" object from the payload
            if (!payload.TryGetProperty("part", out var partEl))
                return false;

            if (partEl.ValueKind != JsonValueKind.Object)
                return false;

            // Extract messageID (uppercase D) from the part object
            if (!partEl.TryGetProperty("messageID", out var messageIdEl))
                return false;

            var messageId = messageIdEl.GetString();
            if (string.IsNullOrEmpty(messageId))
                return false;

            // Deserialize the part manually to tolerate discriminator ordering differences
            // in both the outer message part and nested tool state payloads.
            OpenCodeMessagePart? openCodePart = OpenCodePartDeserializer.DeserializePart(partEl);

            if (openCodePart is null)
                return false; // Unsupported or unknown part type

            // Map to Fleet MessagePart
            var fleetPart = OpenCodeMapper.MapPart(openCodePart);
            if (fleetPart is null)
                return false; // Mapper returned null (e.g. text part with null Text)

            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            using var scope = _scopeFactory.CreateScope();
            var messageRepo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            var sessionActivityWriteService = scope.ServiceProvider.GetRequiredService<SessionActivityWriteService>();

            var existing = await messageRepo.GetByIdAsync(messageId, fleetSessionId).ConfigureAwait(false);

            // Try to extract agent and role from the event info (if present)
            string? agentName = null;
            string? role = null;
            if (payload.TryGetProperty("info", out var infoEl))
            {
                if (infoEl.TryGetProperty("agent", out var agentEl))
                    agentName = agentEl.GetString();

                if (infoEl.TryGetProperty("role", out var roleEl) && roleEl.GetString() is { } rawRole)
                    role = rawRole is "user" or "assistant" ? rawRole : null;
            }

            PersistedMessage persisted;
            if (existing is null)
            {
                // Create new skeleton message for this part
                var partsJson = fleetPart is ReasoningPart
                    ? "[]"
                    : JsonSerializer.Serialize(
                        new[] { fleetPart }, MessagePersistenceService.SerializerOptions);
                persisted = new PersistedMessage
                {
                    Id = messageId,
                    SessionId = fleetSessionId,
                    Role = role ?? "assistant",
                    PartsJson = partsJson,
                    Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    AgentName = agentName,
                };
            }
            else
            {
                persisted = MessagePersistenceService.MergePartAndMetadata(existing, fleetPart, role, agentName);
            }

            ClearBufferedTextDelta(fleetSessionId, messageId, openCodePart.Id);

            await WriteDurableEventAsync(fleetSessionId, sessionActivityWriteService, evt, persisted).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // Silent failure — persistence must never crash event stream
            LogPersistFailed(_logger, _fleetSessionId, ex);
            return false;
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

    private async Task WriteDurableEventAsync(
        string fleetSessionId,
        SessionActivityWriteService sessionActivityWriteService,
        HarnessEvent evt,
        PersistedMessage persistedMessage)
    {
        var durablePayload = evt.Type == "message.updated"
            ? MessagePersistenceService.BuildCommittedMessagePayload(persistedMessage)
            : MessagePersistenceService.SanitizeDurableEventPayload(evt.Type, evt.Payload);

        var createdAt = DateTimeOffset.UtcNow.ToString("O");
        await sessionActivityWriteService.WriteAsync(
            new SessionActivityWriteRequest
            {
                MessagesToUpsert = [persistedMessage],
                OutboxMessages = durablePayload.HasValue
                    ?
                    [
                        new OutboxMessage
                        {
                            Topic = $"session:{fleetSessionId}",
                            Type = evt.Type,
                            Payload = MessagePersistenceService.SerializePayload(durablePayload.Value),
                            UserId = _ownerUserId,
                            CreatedAt = createdAt,
                            AvailableAt = createdAt
                        }
                    ]
                    : []
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private void BufferTextDelta(string fleetSessionId, HarnessEvent evt)
    {
        if (evt.Type != "message.part.delta" || !evt.Payload.HasValue || evt.Payload.Value.ValueKind != JsonValueKind.Object)
            return;

        var payload = evt.Payload.Value;
        if (!payload.TryGetProperty("field", out var fieldEl) || fieldEl.GetString() != "text")
            return;

        if (!payload.TryGetProperty("messageID", out var messageIdEl)
            || !payload.TryGetProperty("partID", out var partIdEl)
            || !payload.TryGetProperty("delta", out var deltaEl))
            return;

        var messageId = messageIdEl.GetString();
        var partId = partIdEl.GetString();
        var delta = deltaEl.GetString();
        if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(partId) || string.IsNullOrEmpty(delta))
            return;

        _bufferedTextDeltas.AddOrUpdate((BuildBufferedDeltaKey(fleetSessionId, messageId, partId), partId), delta, (_, existing) => existing + delta);
    }

    private PersistedMessage ApplyBufferedTextDeltaIfPresent(
        string fleetSessionId,
        PersistedMessage existing,
        string messageId,
        string role,
        string? agentName)
    {
        var prefix = BuildBufferedDeltaPrefix(fleetSessionId, messageId);
        var matchingEntries = _bufferedTextDeltas
            .Where(entry => entry.Key.MessageKey.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();

        if (matchingEntries.Length == 0)
            return MessagePersistenceService.MergeMetadata(existing, role, agentName);

        var merged = existing;
        foreach (var entry in matchingEntries)
        {
            merged = MessagePersistenceService.MergeTextDeltaAndMetadata(merged, entry.Value, role, agentName);
            _bufferedTextDeltas.TryRemove(entry.Key, out _);
        }

        return merged;
    }

    private void ClearBufferedTextDelta(string fleetSessionId, string messageId, string partId)
    {
        if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(partId))
            return;

        _bufferedTextDeltas.TryRemove((BuildBufferedDeltaKey(fleetSessionId, messageId, partId), partId), out _);
    }

    private static string BuildBufferedDeltaPrefix(string fleetSessionId, string messageId)
        => $"{fleetSessionId}::{messageId}::";

    private static string BuildBufferedDeltaKey(string fleetSessionId, string messageId, string partId)
        => $"{BuildBufferedDeltaPrefix(fleetSessionId, messageId)}{partId}";
}
