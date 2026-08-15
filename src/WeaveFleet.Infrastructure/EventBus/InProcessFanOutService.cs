#pragma warning disable CA1848, CA1873 // Temporary diagnostic logging
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// Background service that reads every event (durable + ephemeral) from the in-process fan-out
/// channel and broadcasts to <see cref="IEventBroadcaster"/> on the per-session topic.
/// Activity-status events (tracker update + global "sessions" topic broadcast) are handled
/// directly in <see cref="HarnessEventRelay"/> to avoid lossy channel drops.
/// </summary>
internal sealed partial class InProcessFanOutService : BackgroundService
{
    private readonly InProcessChannels _channels;
    private readonly IEventBroadcaster _broadcaster;
    private readonly PipelineLatencyMetrics _pipelineMetrics;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InProcessFanOutService> _logger;

    public InProcessFanOutService(
        InProcessChannels channels,
        IEventBroadcaster broadcaster,
        PipelineLatencyMetrics pipelineMetrics,
        IServiceScopeFactory scopeFactory,
        ILogger<InProcessFanOutService> logger)
    {
        _channels = channels;
        _broadcaster = broadcaster;
        _pipelineMetrics = pipelineMetrics;
        _scopeFactory = scopeFactory; // Still needed for EnrichSessionStatusPayloadAsync
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
        using var activity = FleetInstrumentation.ActivitySource.StartActivity(
            "fleet.fanout",
            ActivityKind.Internal,
            envelope.TraceContext ?? default);

        var fanoutStart = Stopwatch.GetTimestamp();
        var sessionId = envelope.SessionId;
        var eventType = envelope.EventType;
        var userId    = envelope.UserId;
        var evt       = envelope.Event;
        var domainEvent = envelope.DomainEvent;
        var classification = EventTypeMetadata.Classify(eventType);

        activity?.SetTag(FleetInstrumentation.SessionIdTag, sessionId);
        activity?.SetTag("event.type", eventType);

        _logger.LogDebug("[FanOut] type={Type} session={Session} user={User} isDurable={IsDurable}",
            eventType, sessionId, userId, envelope.IsDurable);

        if (IsUserMessageEcho(evt))
        {
            _logger.LogDebug("[FanOut] Skipped user message echo type={Type}", eventType);
            return;
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

        _pipelineMetrics.RecordFanoutHop(Stopwatch.GetElapsedTime(fanoutStart).TotalMilliseconds, eventType);

        _logger.LogDebug("[FanOut] Broadcast topic=session:{Session} type={Type} advisory={Advisory}",
            sessionId, eventType, classification.IsAdvisory);
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
        var instanceTracker = scope.ServiceProvider.GetRequiredService<InstanceTracker>();
        var session = await sessionRepository.GetByIdAsync(sessionId).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        if (session is null)
        {
            return SessionCapabilitiesResolver.Resolve(null, null, null, activityStatus, isLive: false);
        }

        // Use the parsed activityStatus from the event (not the tracker) to compute capabilities
        // for this specific broadcast. The tracker may not yet reflect this status change.
        var isLive = instanceTracker.Get(session.InstanceId) is not null;
        return SessionCapabilitiesResolver.Resolve(
            session.RuntimeMode,
            session.LifecycleStatus,
            session.RetentionStatus,
            activityStatus ?? "idle",
            isLive);
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
