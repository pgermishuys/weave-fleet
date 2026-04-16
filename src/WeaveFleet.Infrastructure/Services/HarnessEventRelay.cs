using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Background service that bridges ephemeral harness instance events to <see cref="IEventBroadcaster"/>.
/// Subscribes to <see cref="InstanceTracker"/> registration/removal events and maintains
/// one async-enumerable pump per live instance.
/// Durable activity flows through <see cref="HarnessEventPersistenceService"/> which is
/// created once per pump lifetime and called for every event before ephemeral relay.
/// </summary>
public sealed class HarnessEventRelay : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> LogSessionNotFound =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "SessionNotFound"),
            "Could not resolve fleet session for instance {InstanceId} after retries");

    private static readonly Action<ILogger, string, Exception?> LogPumpFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(2, "PumpFailed"),
            "Event pump failed for instance {InstanceId}");

    private readonly InstanceTracker _tracker;
    private readonly IEventBroadcaster _broadcaster;
    private readonly SessionActivityTracker _activityTracker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HarnessEventRelay> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _subscriptions = new();
    private CancellationToken _stoppingToken;

    // Ephemeral event policy:
    // - session.status / session.idle
    //   Live process/session status indicators for UX. These are not durable facts
    //   about conversation history and can be recomputed or rediscovered.
    // - message.part.delta
    //   High-frequency streaming preview chunks while a message is being generated.
    //   These intentionally bypass DB/outbox; only the authoritative
    //   message.created/message.updated/message.part.updated snapshots are persisted.
    // - error
    //   Live transport/runtime notification, not durable session history.
    // - permission.*
    //   UI capability/control-plane signals, not conversation state.
    //
    // Durable events (message.created, message.updated, message.part.updated,
    // message.removed, message.part.removed, session.updated, session.error,
    // session.compacted, session.deleted) are handled by HarnessEventPersistenceService
    // and are NOT directly relayed to the broadcaster.
    private static bool IsEphemeralRelayEvent(string eventType)
        => EventTypeMetadata.Classify(eventType).IsEphemeralRelay;

    public HarnessEventRelay(
        InstanceTracker tracker,
        IEventBroadcaster broadcaster,
        SessionActivityTracker activityTracker,
        IServiceScopeFactory scopeFactory,
        ILogger<HarnessEventRelay> logger)
    {
        _tracker = tracker;
        _broadcaster = broadcaster;
        _activityTracker = activityTracker;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        _tracker.InstanceRegistered += OnInstanceRegistered;
        _tracker.InstanceRemoved += OnInstanceRemoved;

        // Subscribe to any already-running instances (handles service restart scenario)
        foreach (var (id, instance) in _tracker.GetAll())
            StartSubscription(id, instance);

        // Keep alive until shutdown, then clean up event handlers
        return Task.Delay(Timeout.Infinite, stoppingToken)
            .ContinueWith(_ =>
            {
                _tracker.InstanceRegistered -= OnInstanceRegistered;
                _tracker.InstanceRemoved -= OnInstanceRemoved;
            }, TaskScheduler.Default);
    }

    private void OnInstanceRegistered(string instanceId, IHarnessSession instance)
    {
        StartSubscription(instanceId, instance);
    }

    private void OnInstanceRemoved(string instanceId)
    {
        if (_subscriptions.TryRemove(instanceId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void StartSubscription(string instanceId, IHarnessSession instance)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
        if (!_subscriptions.TryAdd(instanceId, cts))
        {
            cts.Dispose();
            return; // already subscribed
        }

        _ = Task.Run(() => PumpAsync(instanceId, instance, cts.Token), cts.Token);
    }

    private async Task PumpAsync(string instanceId, IHarnessSession instance, CancellationToken ct)
    {
        // Look up fleet session ID with retry to handle the race condition where
        // InstanceTracker.Register() fires before ISessionRepository.InsertAsync() completes.
        string? fleetSessionId = null;
        string? sessionUserId = null;
        for (int attempt = 0; attempt < 10 && !ct.IsCancellationRequested; attempt++)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var session = await repo.GetAnyForInstanceAsync(instanceId).ConfigureAwait(false);
            if (session is not null)
            {
                fleetSessionId = session.Id;
                sessionUserId = session.UserId;
                break;
            }

            try
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (fleetSessionId is null)
        {
            LogSessionNotFound(_logger, instanceId, null);
            return;
        }

        // Create one persistence service per pump lifetime — it owns the delta buffer
        // for this instance and must outlive individual events.
        // The pump scope is kept alive for the entire pump lifetime so that the scoped
        // repositories and services resolved from it are not disposed prematurely.
        IServiceScope? pumpScope = null;
        HarnessEventPersistenceService? persistenceService = null;
        if (sessionUserId is not null)
        {
            pumpScope = _scopeFactory.CreateScope();
            var messageRepo = pumpScope.ServiceProvider.GetRequiredService<IMessageRepository>();
            var sessionRepo = pumpScope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var activityWriteService = pumpScope.ServiceProvider.GetRequiredService<SessionActivityWriteService>();
            persistenceService = new HarnessEventPersistenceService(
                messageRepo,
                sessionRepo,
                activityWriteService,
                sessionUserId);
        }

        try
        {
            await foreach (var evt in instance.SubscribeAsync(ct).ConfigureAwait(false))
            {
                var targetFleetSessionId = evt.FleetSessionId ?? fleetSessionId;
                var targetTopic = $"session:{targetFleetSessionId}";

                // Buffer text deltas before persistence so they are available when
                // message.updated arrives and merges the buffered content.
                persistenceService?.BufferTextDelta(targetFleetSessionId, evt);

                // Attempt durable persistence for all events. Returns true if the event
                // was handled durably and should NOT be relayed as ephemeral.
                var handledDurably = persistenceService is not null
                    && await persistenceService.TryHandleDurableEventAsync(targetFleetSessionId, evt).ConfigureAwait(false);

                if (handledDurably)
                    continue;

                if (!IsEphemeralRelayEvent(evt.Type))
                    continue;

                // Guard against null Payload — BroadcastAsync serializes via
                // JsonSerializer.SerializeToElement which throws on null/Undefined JsonElement.
                // Use an empty object {} as fallback when Payload is null.
                // Preserve the source payload session IDs. Upstream harness instances are
                // responsible for filtering events so only the correct Fleet session is
                // broadcast on this topic.
                object payload = evt.Payload.HasValue
                    ? evt.Payload.Value
                    : JsonSerializer.SerializeToElement(new { });
                await _broadcaster.BroadcastAsync(targetTopic, evt.Type, payload, sessionUserId, ct).ConfigureAwait(false);

                // Track activity status and broadcast on the global "sessions" topic so
                // the sidebar activity stream receives it. This also populates the
                // SessionActivityTracker for initial-state snapshots on WebSocket subscribe.
                var activityStatus = ParseActivityStatus(evt.Type, evt.Payload);
                if (activityStatus is not null)
                {
                    _activityTracker.Update(targetFleetSessionId, activityStatus, sessionUserId);
                    await _broadcaster.BroadcastAsync(
                        "sessions",
                        "activity_status",
                        new { sessionId = targetFleetSessionId, activityStatus },
                        sessionUserId,
                        ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on instance removal or application shutdown
        }
        catch (Exception ex)
        {
            LogPumpFailed(_logger, instanceId, ex);
        }
        finally
        {
            if (_subscriptions.TryRemove(instanceId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            // Flush any buffered text deltas so partial streaming content is not lost
            // when the harness disconnects or crashes.
            if (persistenceService is not null)
            {
                try
                {
                    await persistenceService.FlushBufferedDeltasAsync(fleetSessionId).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Best-effort flush — never crash the pump cleanup
                }
            }

            // When the harness disconnects, clear the activity state and broadcast idle
            // so the UI doesn't show a session stuck on "busy" after a crash or disconnect.
            _activityTracker.Remove(fleetSessionId);
            await _broadcaster.BroadcastAsync(
                "sessions",
                "activity_status",
                new { sessionId = fleetSessionId, activityStatus = "idle" },
                sessionUserId,
                CancellationToken.None).ConfigureAwait(false);

            // Dispose the pump scope after all cleanup is complete so that the scoped
            // repositories and services remain valid throughout the flush above.
            pumpScope?.Dispose();
        }
    }

    /// <summary>
    /// Parses the activity status string from a harness event.
    /// Returns <c>"busy"</c>, <c>"idle"</c>, or <c>null</c> if the event is not an activity event.
    /// </summary>
    private static string? ParseActivityStatus(string eventType, JsonElement? payload)
    {
        if (eventType == EventTypes.SessionIdle)
            return "idle";

        if (eventType == EventTypes.SessionStatus && payload.HasValue)
        {
            // Payload shape: { "status": { "type": "busy" | "idle" | ... } }
            if (payload.Value.TryGetProperty("status", out var statusProp)
                && statusProp.TryGetProperty("type", out var typeProp))
            {
                return typeProp.GetString();
            }
        }

        return null;
    }
}
