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
/// Background service that bridges harness instance events to <see cref="IEventBroadcaster"/>.
/// Subscribes to <see cref="InstanceTracker"/> registration/removal events and maintains
/// one async-enumerable pump per live instance.
/// The relay is a pure broadcast service — all message persistence is handled by each
/// <see cref="IHarnessSession"/> implementation (instance-owned persistence pattern).
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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HarnessEventRelay> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _subscriptions = new();
    private CancellationToken _stoppingToken;

    public HarnessEventRelay(
        InstanceTracker tracker,
        IEventBroadcaster broadcaster,
        IServiceScopeFactory scopeFactory,
        ILogger<HarnessEventRelay> logger)
    {
        _tracker = tracker;
        _broadcaster = broadcaster;
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

        var topic = $"session:{fleetSessionId}";
        try
        {
            await foreach (var evt in instance.SubscribeAsync(ct).ConfigureAwait(false))
            {
                var targetFleetSessionId = evt.FleetSessionId ?? fleetSessionId;
                var targetTopic = $"session:{targetFleetSessionId}";

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
        }
    }
}

