#pragma warning disable CA1848, CA1873 // Temporary diagnostic logging
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Events;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Parsed activity status with optional retry metadata.
/// </summary>
internal sealed record ParsedActivityStatus(
    string Status,
    int? RetryAttempt = null,
    string? RetryMessage = null,
    DateTimeOffset? RetryNext = null);

/// <summary>
/// Background service that subscribes to <see cref="InstanceTracker"/> registration/removal
/// events and maintains one async-enumerable pump per live harness instance. Each pump:
/// <list type="number">
///   <item>Resolves the Fleet session metadata (id, owner, project, harness-type).</item>
///   <item>Applies the reasoning-content filter before publish for event types whose
///     classification requires it, so unsanitized reasoning never reaches event bus subscribers.</item>
///   <item>Publishes every <see cref="HarnessEvent"/> via <see cref="IEventPublisher"/>
///     with an internal per-pump monotonic dedup key.</item>
///   <item>On disconnect: emits a final idle broadcast on the global <c>sessions</c> topic.</item>
/// </list>
/// The relay is publish-only — WebSocket fan-out for every event is handled by
/// <c>InProcessFanOutService</c>.
/// </summary>
public sealed class HarnessEventRelay : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> _logSessionNotFound =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "SessionNotFound"),
            "Could not resolve fleet session for instance {InstanceId} after retries");

    private static readonly Action<ILogger, string, Exception?> _logPumpFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(2, "PumpFailed"),
            "Event pump failed for instance {InstanceId}");

    private static readonly Action<ILogger, string, Exception?> _logPublishFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(3, "EventPublishFailed"),
            "Event publish failed for instance {InstanceId}");

    private static readonly Action<ILogger, int, Exception?> _logShutdownTimeout =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(4, "ShutdownTimeout"),
            "Shutdown timed out waiting for {Count} pump task(s) to complete");

    private readonly InstanceTracker _tracker;
    private readonly IEventBroadcaster _broadcaster;
    private readonly IEventPublisher _publisher;
    private readonly SessionActivityTracker _activityTracker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HarnessEventRelay> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _subscriptions = new();
    private readonly ConcurrentDictionary<string, Task> _pumpTasks = new();
    private readonly ConcurrentDictionary<string, long> _internalPumpDedupKeys = new();
    private CancellationToken _stoppingToken;

    public HarnessEventRelay(
        InstanceTracker tracker,
        IEventBroadcaster broadcaster,
        IEventPublisher publisher,
        SessionActivityTracker activityTracker,
        IServiceScopeFactory scopeFactory,
        ILogger<HarnessEventRelay> logger)
    {
        _tracker = tracker;
        _broadcaster = broadcaster;
        _publisher = publisher;
        _activityTracker = activityTracker;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        _tracker.InstanceRegistered += OnInstanceRegistered;
        _tracker.InstanceRemoved += OnInstanceRemoved;

        // Subscribe to any already-running instances (handles service restart scenario)
        foreach (var (id, instance) in _tracker.GetAll())
            StartSubscription(id, instance);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        finally
        {
            _tracker.InstanceRegistered -= OnInstanceRegistered;
            _tracker.InstanceRemoved -= OnInstanceRemoved;

            // Wait for all pump tasks to finish their cleanup (flush deltas, broadcast idle),
            // but cap the wait so a stuck subscription never blocks shutdown indefinitely.
            var tasks = _pumpTasks.Values.ToArray();
            if (tasks.Length > 0)
            {
                var allPumps = Task.WhenAll(tasks);
                var winner = await Task.WhenAny(allPumps, Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None))
                    .ConfigureAwait(false);

                if (winner != allPumps)
                {
                    _logShutdownTimeout(_logger, tasks.Count(t => !t.IsCompleted), null);
                }
            }
        }
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
            return;
        }

        var task = Task.Run(() => PumpAsync(instanceId, instance, cts.Token), cts.Token);
        _pumpTasks.TryAdd(instanceId, task);

        // Remove from tracking when pump completes
        _ = task.ContinueWith(
            _ => _pumpTasks.TryRemove(instanceId, out Task? _),
            TaskScheduler.Default);
    }

    private async Task PumpAsync(string instanceId, IHarnessSession instance, CancellationToken ct)
    {
        using var pumpActivity = FleetInstrumentation.ActivitySource.StartActivity(
            "fleet.relay.pump",
            ActivityKind.Consumer);
        pumpActivity?.SetTag("instance.id", instanceId);

        _logger.LogDebug("[Relay:Pump] Starting pump for instance={InstanceId} type={Type}", instanceId, instance.GetType().Name);
        // Resolve fleet session metadata with retry to handle the race where
        // InstanceTracker.Register() fires before ISessionRepository.InsertAsync() completes.
        string? fleetSessionId = null;
        string? sessionUserId = null;
        string? sessionProjectId = null;
        string? sessionHarnessType = null;
        string? sessionSourceReference = null;
        for (int attempt = 0; attempt < 10 && !ct.IsCancellationRequested; attempt++)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var session = await repo.GetAnyForInstanceAsync(instanceId).ConfigureAwait(false);
            if (session is not null)
            {
                fleetSessionId = session.Id;
                sessionUserId = session.UserId;
                sessionProjectId = session.ProjectId;
                sessionHarnessType = session.HarnessType;
                sessionSourceReference = session.SourceReference;
                _logger.LogDebug("[Relay:Pump] Resolved session for instance={InstanceId}: fleetSession={FleetSession} harness={HarnessType}", instanceId, fleetSessionId, sessionHarnessType);
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
            _logger.LogWarning("[Relay:Pump] Session not found for instance={InstanceId} after 10 retries — aborting pump", instanceId);
            _logSessionNotFound(_logger, instanceId, null);
            return;
        }

        pumpActivity?.SetTag(FleetInstrumentation.SessionIdTag, fleetSessionId);
        pumpActivity?.SetTag("harness.type", sessionHarnessType);

        var suppressedUserMessageIds = new HashSet<string>(StringComparer.Ordinal);
        using var translationScope = _scopeFactory.CreateScope();
        var translator = translationScope.ServiceProvider.GetRequiredService<DomainEventTranslator>();

        // Resync activity status on pump start (handles reconnect gaps where busy→idle was missed).
        // Query the harness's current status and seed the tracker + broadcast if different.
        await ResyncActivityStatusAsync(instance, fleetSessionId, sessionUserId, ct).ConfigureAwait(false);

        try
        {
            await foreach (var evt in instance.SubscribeAsync(ct).ConfigureAwait(false))
            {
                _logger.LogDebug("[Relay:Pump] Received event type={Type} session={Session} instance={Instance}", evt.Type, evt.SessionId, instanceId);
                var targetFleetSessionId = evt.FleetSessionId ?? fleetSessionId;

                // Apply the reasoning-content filter BEFORE publishing — the unified fan-out
                // subscriber forwards the published payload directly to WebSocket clients, so
                // unsanitized reasoning must never leave this method. Null from the sanitizer
                // means "reasoning-only part; drop the event entirely".
                var classification = EventTypeMetadata.Classify(evt.Type);
                HarnessEvent eventToPublish = evt;
                if (classification.RequiresReasoningFilter)
                {
                    var filteredPayload = ReasoningFilter.SanitizeEventPayload(evt.Type, evt.Payload);
                    if (filteredPayload is null)
                        continue;
                    eventToPublish = evt with { Payload = filteredPayload };
                }

                if (ShouldSuppressUserEcho(eventToPublish, suppressedUserMessageIds))
                {
                    _logger.LogDebug("[Relay:Pump] Suppressed user echo type={Type}", eventToPublish.Type);
                    continue;
                }

                var eventToTranslate = eventToPublish with { FleetSessionId = targetFleetSessionId };
                var domainEvent = translator.Translate(eventToTranslate);
                _logger.LogDebug("[Relay:Pump] Translated type={Type} domainEvent={DomainEvent} targetSession={TargetSession}",
                    evt.Type, domainEvent?.GetType().Name ?? "null", targetFleetSessionId);

                try
                {
                    // Link relay event spans back to the originating prompt trace so you can
                    // navigate from a prompt to all its response events in the trace viewer.
                    var promptCtx = _activityTracker.GetPromptTraceContext(targetFleetSessionId);
                    var links = promptCtx.HasValue
                        ? new[] { new ActivityLink(promptCtx.Value) }
                        : Array.Empty<ActivityLink>();

                    using var eventActivity = FleetInstrumentation.ActivitySource.StartActivity(
                        "fleet.relay.event",
                        ActivityKind.Internal,
                        parentContext: default,
                        links: links);
                    eventActivity?.SetTag(FleetInstrumentation.SessionIdTag, targetFleetSessionId);
                    eventActivity?.SetTag("event.type", evt.Type);

                    var traceContext = Activity.Current?.Context;
                    var pumpDedupKey = NextInternalPumpDedupKey(instanceId);
                    _ = await _publisher.PublishAsync(
                        eventToPublish,
                        new EventPublishContext(
                            targetFleetSessionId,
                            sessionProjectId,
                            sessionUserId,
                            sessionHarnessType,
                            pumpDedupKey)
                        {
                            DomainEvent = domainEvent,
                            SourceReference = sessionSourceReference,
                            TraceContext = traceContext
                        },
                        ct).ConfigureAwait(false);

                    // Handle activity_status events directly in the relay to avoid lossy channel drops.
                    // The per-session broadcast still happens via InProcessFanOutService, but the
                    // global "sessions" topic broadcast + tracker update happen here synchronously.
                    var parsedStatus = ParseActivityStatus(evt.Type, evt.Payload);
                    if (parsedStatus is not null)
                    {
                        _activityTracker.Update(
                            targetFleetSessionId,
                            parsedStatus.Status,
                            sessionUserId,
                            parsedStatus.RetryAttempt,
                            parsedStatus.RetryMessage,
                            parsedStatus.RetryNext);

                        await _broadcaster.BroadcastAsync(
                            "sessions",
                            "activity_status",
                            await BuildActivityStatusPayloadAsync(
                                targetFleetSessionId,
                                parsedStatus.Status,
                                parsedStatus.RetryAttempt,
                                parsedStatus.RetryMessage,
                                parsedStatus.RetryNext).ConfigureAwait(false),
                            sessionUserId,
                            ct).ConfigureAwait(false);

                        await SessionPropagation.PropagateToParentAsync(
                            targetFleetSessionId, sessionUserId, _activityTracker, _broadcaster, _scopeFactory, ct)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception pubEx)
                {
                    _logPublishFailed(_logger, instanceId, pubEx);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on instance removal or application shutdown
        }
        catch (Exception ex)
        {
            _logPumpFailed(_logger, instanceId, ex);
        }
        finally
        {
            if (_subscriptions.TryRemove(instanceId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            // Clear activity state and broadcast idle so the UI doesn't show a session stuck
            // on "busy" after a crash/disconnect. This isn't tied to an event, so it stays in
            // the relay (the only code that knows when a pump ends).
            _activityTracker.Remove(fleetSessionId);
            _activityTracker.ClearPromptTraceContext(fleetSessionId);
            await _broadcaster.BroadcastAsync(
                "sessions",
                "activity_status",
                await BuildActivityStatusPayloadAsync(fleetSessionId, "idle").ConfigureAwait(false),
                sessionUserId,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> BuildActivityStatusPayloadAsync(
        string sessionId,
        string activityStatus,
        int? retryAttempt = null,
        string? retryMessage = null,
        DateTimeOffset? retryNext = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var sessionRepository = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var capabilitiesResolver = scope.ServiceProvider.GetRequiredService<SessionCapabilitiesResolver>();
        var session = await sessionRepository.GetByIdAsync(sessionId).ConfigureAwait(false);
        if (session is not null)
        {
            session.ActivityStatus = activityStatus;
        }

        var capabilities = session is not null
            ? capabilitiesResolver.Resolve(session)
            : SessionCapabilitiesResolver.Resolve(null, null, null, activityStatus, isLive: false);

        return InfrastructureJsonContext.SerializeActivityStatus(
            sessionId,
            activityStatus,
            capabilities,
            retryAttempt,
            retryMessage,
            retryNext);
    }

    private static ParsedActivityStatus? ParseActivityStatus(string eventType, JsonElement? payload)
    {
        if (eventType == EventTypes.SessionIdle)
            return new ParsedActivityStatus("idle");

        if (eventType == EventTypes.SessionStatus && payload.HasValue
            && payload.Value.TryGetProperty("status", out var statusProp)
            && statusProp.TryGetProperty("type", out var typeProp))
        {
            var statusType = typeProp.GetString();
            if (statusType is null)
                return null;

            // If status is "retry", extract retry metadata
            if (statusType == "retry")
            {
                var attempt = statusProp.TryGetProperty("count", out var countProp) && countProp.TryGetInt32(out var c) ? c : (int?)null;
                var message = statusProp.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() : null;
                DateTimeOffset? next = null;

                if (statusProp.TryGetProperty("delay", out var delayProp) && delayProp.TryGetInt32(out var delayMs))
                {
                    next = DateTimeOffset.UtcNow.AddMilliseconds(delayMs);
                }

                return new ParsedActivityStatus(statusType, attempt, message, next);
            }

            return new ParsedActivityStatus(statusType);
        }

        return null;
    }

    private long NextInternalPumpDedupKey(string instanceId)
        => _internalPumpDedupKeys.AddOrUpdate(instanceId, 1, (_, current) => current + 1);

    private static bool ShouldSuppressUserEcho(HarnessEvent evt, HashSet<string> suppressedUserMessageIds)
    {
        if (evt.Type is EventTypes.MessageCreated or EventTypes.MessageUpdated)
        {
            var userMessageId = TryGetUserMessageId(evt.Payload);
            if (userMessageId is null)
                return false;

            suppressedUserMessageIds.Add(userMessageId);
            return true;
        }

        if (evt.Type is not (EventTypes.MessagePartUpdated or EventTypes.MessagePartDelta))
            return false;

        var partMessageId = TryGetPartMessageId(evt.Payload);
        return partMessageId is not null && suppressedUserMessageIds.Contains(partMessageId);
    }

    private static string? TryGetUserMessageId(System.Text.Json.JsonElement? payload)
    {
        if (!payload.HasValue
            || payload.Value.ValueKind != System.Text.Json.JsonValueKind.Object
            || !payload.Value.TryGetProperty("info", out var info)
            || info.ValueKind != System.Text.Json.JsonValueKind.Object
            || !info.TryGetProperty("role", out var role)
            || role.GetString() is not "user"
            || !info.TryGetProperty("id", out var id))
        {
            return null;
        }

        return id.GetString();
    }

    private static string? TryGetPartMessageId(System.Text.Json.JsonElement? payload)
    {
        if (!payload.HasValue
            || payload.Value.ValueKind != System.Text.Json.JsonValueKind.Object
            || !payload.Value.TryGetProperty("part", out var part)
            || part.ValueKind != System.Text.Json.JsonValueKind.Object
            || !part.TryGetProperty("messageID", out var messageId))
        {
            return null;
        }

        return messageId.GetString();
    }

    /// <summary>
    /// Queries the harness instance's current activity status and seeds the tracker + broadcasts
    /// a correction if the status differs from the tracker's current value. This ensures that
    /// if a busy→idle transition was missed during a reconnect gap, the state is corrected.
    /// </summary>
    private async Task ResyncActivityStatusAsync(
        IHarnessSession instance,
        string fleetSessionId,
        string? sessionUserId,
        CancellationToken ct)
    {
        try
        {
            var currentActivityStatus = await instance.GetActivityStatusAsync(ct).ConfigureAwait(false);
            if (currentActivityStatus is null)
            {
                // Harness doesn't support activity status queries, skip resync
                return;
            }

            // Check if the tracker's current status differs from the queried status
            var trackedSnapshot = _activityTracker.Get(fleetSessionId);
            if (trackedSnapshot?.ActivityStatus != currentActivityStatus)
            {
                // Update tracker and broadcast correction
                _activityTracker.Update(fleetSessionId, currentActivityStatus, sessionUserId);
                await _broadcaster.BroadcastAsync(
                    "sessions",
                    "activity_status",
                    await BuildActivityStatusPayloadAsync(fleetSessionId, currentActivityStatus).ConfigureAwait(false),
                    sessionUserId,
                    ct).ConfigureAwait(false);

                _logger.LogDebug(
                    "[Relay:Resync] Corrected activity status for session={Session} from {Old} to {New}",
                    fleetSessionId,
                    trackedSnapshot?.ActivityStatus ?? "null",
                    currentActivityStatus);
            }
        }
        catch (Exception ex)
        {
            // Best-effort resync — never crash the pump
            _logger.LogWarning(ex, "[Relay:Resync] Failed to resync activity status for session={Session}", fleetSessionId);
        }
    }
}
