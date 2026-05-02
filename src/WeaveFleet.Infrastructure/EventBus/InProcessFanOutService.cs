using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;

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

        // Buffer message.part.delta text for the durable merge on next message.updated.
        if (evt.Type == EventTypes.MessagePartDelta && userId is not null)
        {
            using var scope = _scopeFactory.CreateScope();
            var persister = scope.ServiceProvider.GetRequiredService<IHarnessEventPersister>();
            persister.BufferTextDelta(sessionId, evt);
        }

        // Fan out to the broadcaster on the per-session WebSocket topic.
        object payload = evt.Payload.HasValue
            ? evt.Payload.Value
            : JsonSerializer.SerializeToElement(new { });

        await _broadcaster.BroadcastAsync(
            $"session:{sessionId}", eventType, payload, envelope.Sequence, userId, ct)
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
                ct).ConfigureAwait(false);

            await Services.SessionPropagation.PropagateToParentAsync(
                sessionId, userId, _activityTracker, _broadcaster, ct)
                .ConfigureAwait(false);
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

    [LoggerMessage(Level = LogLevel.Warning, EventId = 1,
        Message = "In-process fan-out failed to forward event to broadcaster.")]
    private static partial void LogFanOutFailed(ILogger logger, Exception ex);
}
