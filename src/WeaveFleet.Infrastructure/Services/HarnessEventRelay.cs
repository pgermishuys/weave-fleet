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

    private void OnInstanceRegistered(string instanceId, IHarnessInstance instance)
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

    private void StartSubscription(string instanceId, IHarnessInstance instance)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
        if (!_subscriptions.TryAdd(instanceId, cts))
        {
            cts.Dispose();
            return; // already subscribed
        }

        _ = Task.Run(() => PumpAsync(instanceId, instance, cts.Token), cts.Token);
    }

    private async Task PumpAsync(string instanceId, IHarnessInstance instance, CancellationToken ct)
    {
        // Look up fleet session ID with retry to handle the race condition where
        // InstanceTracker.Register() fires before ISessionRepository.InsertAsync() completes.
        string? fleetSessionId = null;
        for (int attempt = 0; attempt < 10 && !ct.IsCancellationRequested; attempt++)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var session = await repo.GetAnyForInstanceAsync(instanceId).ConfigureAwait(false);
            if (session is not null)
            {
                fleetSessionId = session.Id;
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
                // Guard against null Payload — BroadcastAsync serializes via
                // JsonSerializer.SerializeToElement which throws on null/Undefined JsonElement.
                // Use an empty object {} as fallback when Payload is null.
                // Rewrite sessionId/sessionID fields in the payload to use the Fleet session ID
                // so the frontend's session ID checks work correctly (Fleet ID vs Fleet ID).
                object payload = evt.Payload.HasValue
                    ? RewriteSessionIds(evt.Payload.Value, fleetSessionId)
                    : JsonSerializer.SerializeToElement(new { });
                await _broadcaster.BroadcastAsync(topic, evt.Type, payload, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Rewrites sessionId/sessionID string fields in a JSON payload to use the Fleet session ID.
    /// Works recursively on nested objects. Returns a new JsonElement.
    /// </summary>
    private static JsonElement RewriteSessionIds(JsonElement source, string fleetSessionId)
    {
        if (source.ValueKind != JsonValueKind.Object)
            return source;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteRewritten(writer, source, fleetSessionId);
        }
        return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
    }

    private static void WriteRewritten(Utf8JsonWriter writer, JsonElement element, string fleetSessionId)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    if ((prop.Name == "sessionId" || prop.Name == "sessionID")
                        && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        writer.WriteStringValue(fleetSessionId);
                    }
                    else
                    {
                        WriteRewritten(writer, prop.Value, fleetSessionId);
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteRewritten(writer, item, fleetSessionId);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
