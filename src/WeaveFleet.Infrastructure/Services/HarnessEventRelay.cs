using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Events;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Background service that subscribes to <see cref="InstanceTracker"/> registration/removal
/// events and maintains one async-enumerable pump per live harness instance. Each pump:
/// <list type="number">
///   <item>Resolves the Fleet session metadata (id, owner, project, harness-type).</item>
///   <item>Applies the reasoning-content filter before publish for event types whose
///     classification requires it, so unsanitized reasoning never reaches event bus subscribers.</item>
///   <item>Publishes every <see cref="HarnessEvent"/> via <see cref="IEventPublisher"/>
///     with a per-pump monotonic sequence for dedup.</item>
///   <item>On disconnect: flushes any buffered text deltas through the persister and emits a
///     final idle broadcast on the global <c>sessions</c> topic.</item>
/// </list>
/// The relay is publish-only — durable persistence is handled downstream by
/// <c>MessagePersistenceProjection</c>, and WebSocket fan-out for every event is handled by
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
                var winner = await Task.WhenAny(allPumps, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None))
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
        // Resolve fleet session metadata with retry to handle the race where
        // InstanceTracker.Register() fires before ISessionRepository.InsertAsync() completes.
        string? fleetSessionId = null;
        string? sessionUserId = null;
        string? sessionProjectId = null;
        string? sessionHarnessType = null;
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
            _logSessionNotFound(_logger, instanceId, null);
            return;
        }

        long publishSequence = 0;
        var suppressedUserMessageIds = new HashSet<string>(StringComparer.Ordinal);
        using var translationScope = _scopeFactory.CreateScope();
        var translator = translationScope.ServiceProvider.GetRequiredService<DomainEventTranslator>();

        try
        {
            await foreach (var evt in instance.SubscribeAsync(ct).ConfigureAwait(false))
            {
                var targetFleetSessionId = evt.FleetSessionId ?? fleetSessionId;

                // Apply the reasoning-content filter BEFORE publishing — the unified fan-out
                // subscriber forwards the published payload directly to WebSocket clients, so
                // unsanitized reasoning must never leave this method. Null from the sanitizer
                // means "reasoning-only part; drop the event entirely".
                var classification = EventTypeMetadata.Classify(evt.Type);
                HarnessEvent eventToPublish = evt;
                if (classification.RequiresReasoningFilter)
                {
                    var filteredPayload = MessagePersistenceService.SanitizeDurableEventPayload(evt.Type, evt.Payload);
                    if (filteredPayload is null)
                        continue;
                    eventToPublish = evt with { Payload = filteredPayload };
                }

                if (ShouldSuppressUserEcho(eventToPublish, suppressedUserMessageIds))
                    continue;

                var eventToTranslate = eventToPublish with { FleetSessionId = targetFleetSessionId };
                var domainEvent = translator.Translate(eventToTranslate);

                try
                {
                    var seq = Interlocked.Increment(ref publishSequence);
                    await _publisher.PublishAsync(
                        eventToPublish,
                        new EventPublishContext(
                            targetFleetSessionId,
                            sessionProjectId,
                            sessionUserId,
                            sessionHarnessType,
                            seq)
                        {
                            DomainEvent = domainEvent
                        },
                        ct).ConfigureAwait(false);
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

            // Flush buffered text deltas through the persister so partial streaming content
            // is not lost when the harness disconnects or crashes.
            if (sessionUserId is not null)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var persister = scope.ServiceProvider.GetRequiredService<IHarnessEventPersister>();
                    await persister.FlushBufferedDeltasAsync(fleetSessionId, sessionUserId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Best-effort flush — never crash the pump cleanup
                }
            }

            // Clear activity state and broadcast idle so the UI doesn't show a session stuck
            // on "busy" after a crash/disconnect. This isn't tied to an event, so it stays in
            // the relay (the only code that knows when a pump ends).
            _activityTracker.Remove(fleetSessionId);
            await _broadcaster.BroadcastAsync(
                "sessions",
                "activity_status",
                InfrastructureJsonContext.SerializeActivityStatus(fleetSessionId, "idle"),
                sessionUserId,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

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
}
