#pragma warning disable CA1848, CA1873 // Temporary diagnostic logging
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// Background service that reads every event (durable + ephemeral) from the in-process fan-out
/// channel and handles WebSocket broadcast duties:
/// <list type="bullet">
///   <item>Broadcasts to <see cref="IEventBroadcaster"/> on the per-session topic.</item>
///   <item>Updates <see cref="SessionActivityTracker"/> for activity events and broadcasts on
///     the global <c>sessions</c> topic.</item>
///   <item>Buffers <c>message.part.delta</c> fragments via
///     <see cref="IHarnessEventPersister.BufferTextDelta"/>.</item>
///   <item>Propagates derived parent-session activity state.</item>
/// </list>
/// Uses <see cref="SessionPropagation"/> to propagate parent-session activity state.
/// </summary>
internal sealed partial class InProcessFanOutService : BackgroundService
{
    private readonly InProcessChannels _channels;
    private readonly IEventBroadcaster _broadcaster;
    private readonly SessionActivityTracker _activityTracker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InProcessFanOutService> _logger;

    public InProcessFanOutService(
        InProcessChannels channels,
        IEventBroadcaster broadcaster,
        SessionActivityTracker activityTracker,
        IServiceScopeFactory scopeFactory,
        ILogger<InProcessFanOutService> logger)
    {
        _channels = channels;
        _broadcaster = broadcaster;
        _activityTracker = activityTracker;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var envelope in _channels.FanOut.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ForwardAsync(envelope, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFanOutFailed(_logger, ex);
            }
        }
    }

    private async Task ForwardAsync(InProcessEnvelope envelope, CancellationToken ct)
    {
        var sessionId = envelope.SessionId;
        var eventType = envelope.EventType;
        var userId    = envelope.UserId;
        var evt       = envelope.Event;
        var domainEvent = envelope.DomainEvent;
        var classification = EventTypeMetadata.Classify(eventType);

        _logger.LogDebug("[FanOut] type={Type} session={Session} user={User} isDurable={IsDurable}",
            eventType, sessionId, userId, envelope.IsDurable);

        if (IsUserMessageEcho(evt))
        {
            _logger.LogDebug("[FanOut] Skipped user message echo type={Type}", eventType);
            return;
        }

        // Buffer message.part.delta text for the durable merge on next message.updated.
        if (evt.Type == EventTypes.MessagePartDelta && userId is not null)
        {
            using var scope = _scopeFactory.CreateScope();
            var persister = scope.ServiceProvider.GetRequiredService<IHarnessEventPersister>();
            persister.BufferTextDelta(sessionId, evt);
        }

        var activityStatus = ParseActivityStatus(evt.Type, evt.Payload);

        // Fan out to the broadcaster on the per-session WebSocket topic.
        var payload = evt.Payload.HasValue
            ? evt.Payload.Value
            : InfrastructureJsonContext.EmptyObject;
        if (eventType == EventTypes.SessionStatus)
        {
            payload = await EnrichSessionStatusPayloadAsync(payload, sessionId, activityStatus, ct)
                .ConfigureAwait(false);
        }

        await _broadcaster.BroadcastAsync(
            $"session:{sessionId}", eventType, payload, classification.IsAdvisory ? null : envelope.EventId, domainEvent, userId, ct)
            .ConfigureAwait(false);

        _logger.LogDebug("[FanOut] Broadcast topic=session:{Session} type={Type} advisory={Advisory}",
            sessionId, eventType, classification.IsAdvisory);

        // Activity-status side-channel for the global "sessions" topic.
        if (activityStatus is not null)
        {
            _activityTracker.Update(sessionId, activityStatus, userId);
            await _broadcaster.BroadcastAsync(
                "sessions",
                "activity_status",
                await BuildSessionActivityStatusPayloadAsync(sessionId, activityStatus, ct).ConfigureAwait(false),
                userId,
                ct).ConfigureAwait(false);

            await SessionPropagation.PropagateToParentAsync(
                sessionId, userId, _activityTracker, _broadcaster, _scopeFactory, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> EnrichSessionStatusPayloadAsync(
        JsonElement payload,
        string sessionId,
        string? activityStatus,
        CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || payload.TryGetProperty("capabilities", out _))
        {
            return payload.Clone();
        }

        var capabilities = await ResolveCapabilitiesAsync(sessionId, activityStatus, ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in payload.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WritePropertyName("capabilities");
            JsonSerializer.Serialize(writer, capabilities, InfrastructureJsonContext.Default.SessionActionCapabilities);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    private async Task<WeaveFleet.Domain.DTOs.SessionActionCapabilities> ResolveCapabilitiesAsync(
        string sessionId,
        string? activityStatus,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sessionRepository = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var capabilitiesResolver = scope.ServiceProvider.GetRequiredService<SessionCapabilitiesResolver>();
        var session = await sessionRepository.GetByIdAsync(sessionId).ConfigureAwait(false);
        if (session is not null && activityStatus is not null)
        {
            session.ActivityStatus = activityStatus;
        }

        ct.ThrowIfCancellationRequested();

        return session is not null
            ? capabilitiesResolver.Resolve(session)
            : SessionCapabilitiesResolver.Resolve(null, null, null, activityStatus, isLive: false);
    }

    private async Task<JsonElement> BuildSessionActivityStatusPayloadAsync(
        string sessionId,
        string activityStatus,
        CancellationToken ct)
    {
        var capabilities = await ResolveCapabilitiesAsync(sessionId, activityStatus, ct).ConfigureAwait(false);

        return InfrastructureJsonContext.SerializeActivityStatus(sessionId, activityStatus, capabilities);
    }

    private static string? ParseActivityStatus(string eventType, JsonElement? payload)
    {
        if (eventType == EventTypes.SessionIdle)
            return "idle";
        if (eventType == EventTypes.SessionStatus && payload.HasValue
            && payload.Value.TryGetProperty("status", out var statusProp)
            && statusProp.TryGetProperty("type", out var typeProp))
        {
            return typeProp.GetString();
        }
        return null;
    }

    private static bool IsUserMessageEcho(HarnessEvent evt)
    {
        if (evt.Type is EventTypes.MessageCreated or EventTypes.MessageUpdated)
            return HasUserRole(evt.Payload);

        return false;
    }

    private static bool HasUserRole(JsonElement? payload)
    {
        if (!payload.HasValue
            || payload.Value.ValueKind != JsonValueKind.Object
            || !payload.Value.TryGetProperty("info", out var info)
            || info.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("role", out var role))
        {
            return false;
        }

        return role.GetString() is "user";
    }

    [LoggerMessage(Level = LogLevel.Warning, EventId = 1,
        Message = "In-process fan-out failed to forward event to broadcaster.")]
    private static partial void LogFanOutFailed(ILogger logger, Exception ex);
}
