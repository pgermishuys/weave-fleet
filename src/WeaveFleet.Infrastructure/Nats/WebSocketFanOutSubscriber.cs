using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// Single core NATS subscriber on the full harness-event subject tree. Forwards every event
/// — durable and ephemeral — to the in-process <see cref="IEventBroadcaster"/> on the session
/// WebSocket topic, in publish order. Per-publisher-connection ordering on the relay side
/// combined with NATS's per-subscription delivery ordering gives end-to-end per-session order.
/// <para>
/// Side-channel duties retained from the former ephemeral relay:
/// parses activity status from <c>session.status</c>/<c>session.idle</c> and updates
/// <see cref="SessionActivityTracker"/> + emits a companion <c>activity_status</c> broadcast on
/// the global <c>sessions</c> topic; buffers <c>message.part.delta</c> fragments into the shared
/// <see cref="TextDeltaBuffer"/> via <see cref="IHarnessEventPersister.BufferTextDelta"/> so
/// partial streaming content is preserved if the harness disconnects before the next snapshot.
/// </para>
/// </summary>
public sealed class WebSocketFanOutSubscriber : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, "FanOutForwardFailed"),
            "Failed to forward NATS event to broadcaster");
    private static readonly Action<ILogger, int, Exception?> LogOversize =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(2, "FanOutOversizePayload"),
            "Dropped oversize fan-out payload ({Bytes} bytes)");
    private static readonly Action<ILogger, Exception?> LogMalformed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(3, "FanOutMalformedPayload"),
            "Dropped malformed fan-out payload");

    private readonly Lazy<INatsConnection> _connectionLazy;
    private readonly IEventBroadcaster _broadcaster;
    private readonly SessionActivityTracker _activityTracker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NatsOptions _options;
    private readonly ILogger<WebSocketFanOutSubscriber> _logger;

    public WebSocketFanOutSubscriber(
        Lazy<INatsConnection> connection,
        IEventBroadcaster broadcaster,
        SessionActivityTracker activityTracker,
        IServiceScopeFactory scopeFactory,
        NatsOptions options,
        ILogger<WebSocketFanOutSubscriber> logger)
    {
        _connectionLazy = connection;
        _broadcaster = broadcaster;
        _activityTracker = activityTracker;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = _connectionLazy.Value;
        await foreach (var msg in connection
            .SubscribeAsync<byte[]>(NatsNamingStrategy.FanOutSubscriptionFilter, cancellationToken: stoppingToken)
            .ConfigureAwait(false))
        {
            try
            {
                if (msg.Data is { Length: > 0 } data && data.Length > _options.MaxPayloadBytes)
                {
                    LogOversize(_logger, data.Length, null);
                    continue;
                }

                var parsed = NatsNamingStrategy.ParseSubject(msg.Subject);
                if (parsed is null)
                {
                    LogMalformed(_logger, null);
                    continue;
                }
                var sessionId = parsed.Value.SessionId;
                var eventType = parsed.Value.EventType;

                HarnessEvent? evt;
                try
                {
                    evt = JsonSerializer.Deserialize<HarnessEvent>(msg.Data!);
                }
                catch (JsonException jx)
                {
                    LogMalformed(_logger, jx);
                    continue;
                }
                if (evt is null) { LogMalformed(_logger, null); continue; }

                string? userId = msg.Headers is not null && msg.Headers.TryGetValue("x-fleet-user-id", out var userIdH)
                    ? userIdH.ToString() : null;
                if (string.IsNullOrEmpty(userId)) userId = null;

                // Buffer message.part.delta text for the durable merge on the next message.updated.
                if (evt.Type == EventTypes.MessagePartDelta && userId is not null)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var persister = scope.ServiceProvider.GetRequiredService<IHarnessEventPersister>();
                    persister.BufferTextDelta(sessionId, evt);
                }

                // Fan out to the in-process broadcaster on the WebSocket topic for this session.
                object payload = evt.Payload.HasValue ? evt.Payload.Value : JsonSerializer.SerializeToElement(new { });
                await _broadcaster.BroadcastAsync($"session:{sessionId}", eventType, payload, userId, stoppingToken)
                    .ConfigureAwait(false);

                // Activity-status side-channel for the global "sessions" topic.
                var activityStatus = ParseActivityStatus(evt.Type, evt.Payload);
                if (activityStatus is not null)
                {
                    _activityTracker.Update(sessionId, activityStatus, userId);
                    await _broadcaster.BroadcastAsync(
                        "sessions",
                        "activity_status",
                        new { sessionId, activityStatus },
                        userId,
                        stoppingToken).ConfigureAwait(false);

                    // Propagate derived busy/idle to the parent session when a child changes state.
                    await PropagateToParentAsync(sessionId, userId, _activityTracker, _broadcaster, stoppingToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(_logger, ex);
            }
        }
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

    /// <summary>
    /// After a child session's activity status changes, propagates the derived effective
    /// activity status to its registered parent session (if any). Broadcasts on both the
    /// global <c>sessions</c> topic (for list updates) and the per-session topic (for the
    /// detail view).
    /// </summary>
    internal static async Task PropagateToParentAsync(
        string childSessionId,
        string? userId,
        SessionActivityTracker tracker,
        IEventBroadcaster broadcaster,
        CancellationToken ct)
    {
        var parentSessionId = tracker.GetParentSessionId(childSessionId);
        if (parentSessionId is null)
            return;

        var parentActivityStatus = tracker.GetEffectiveActivityStatus(parentSessionId);
        if (parentActivityStatus is null)
            return;

        await broadcaster.BroadcastAsync(
            "sessions",
            "activity_status",
            new { sessionId = parentSessionId, activityStatus = parentActivityStatus },
            userId,
            ct).ConfigureAwait(false);

        await broadcaster.BroadcastAsync(
            $"session:{parentSessionId}",
            "activity_status",
            new { sessionId = parentSessionId, activityStatus = parentActivityStatus },
            userId,
            ct).ConfigureAwait(false);
    }
}
