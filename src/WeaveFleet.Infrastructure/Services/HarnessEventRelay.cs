using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

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

    private static readonly Action<ILogger, string, Exception?> LogPersistFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, "PersistFailed"),
            "Failed to persist message event for session {SessionId}");
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

                // Fire-and-forget message persistence (must never block the event stream)
                _ = TryPersistMessageAsync(evt, fleetSessionId);
                _ = TryPersistPartAsync(evt, fleetSessionId);
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

    private async Task TryPersistMessageAsync(HarnessEvent evt, string fleetSessionId)
    {
        // Only process message.created events
        if (evt.Type is not "message.created")
            return;

        try
        {
            if (!evt.Payload.HasValue || evt.Payload.Value.ValueKind != JsonValueKind.Object)
                return;

            var payload = evt.Payload.Value;

            // Guard: must have an "info" property
            if (!payload.TryGetProperty("info", out var infoEl))
                return;

            // Guard: only persist user and assistant messages.
            // Read role from raw JSON — avoid polymorphic deserialization of the abstract base type,
            // which requires the discriminator to be the first property in STJ polymorphism.
            if (!infoEl.TryGetProperty("role", out var roleEl))
                return;

            var role = roleEl.GetString();
            if (role is not ("user" or "assistant"))
                return;

            // Deserialize the concrete info type directly (avoids STJ polymorphism ordering issues).
            OpenCodeMessageInfo? info = role == "assistant"
                ? infoEl.Deserialize<OpenCodeAssistantMessage>(OpenCodeJsonOptions.Default)
                : infoEl.Deserialize<OpenCodeUserMessage>(OpenCodeJsonOptions.Default);
            if (info is null) return;

            // Deserialize parts array separately
            IReadOnlyList<OpenCodeMessagePart> parts = [];
            if (payload.TryGetProperty("parts", out var partsEl))
                parts = partsEl.Deserialize<IReadOnlyList<OpenCodeMessagePart>>(OpenCodeJsonOptions.Default) ?? [];

            var openCodeMessage = new OpenCodeMessageWithParts { Info = info, Parts = parts };
            var harnessMessage = OpenCodeMapper.ToHarnessMessage(openCodeMessage);

            using var scope = _scopeFactory.CreateScope();
            var messageRepo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();

            var persisted = MessagePersistenceService.ToPersistedMessage(fleetSessionId, harnessMessage);

            // Don't overwrite existing message that may already have parts from message.part.updated
            if (harnessMessage.Parts.Count == 0)
            {
                var existing = await messageRepo.GetByIdAsync(persisted.Id, persisted.SessionId).ConfigureAwait(false);
                if (existing is not null) return; // Don't overwrite with empty skeleton
            }

            await messageRepo.UpsertAsync(persisted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Silent failure — persistence must never crash event relay
            LogPersistFailed(_logger, fleetSessionId, ex);
        }
    }

    private async Task TryPersistPartAsync(HarnessEvent evt, string fleetSessionId)
    {
        // Only process message.part.updated events
        if (evt.Type is not "message.part.updated")
            return;

        try
        {
            if (!evt.Payload.HasValue || evt.Payload.Value.ValueKind != JsonValueKind.Object)
                return;

            var payload = evt.Payload.Value;

            // Extract the "part" object from the payload
            if (!payload.TryGetProperty("part", out var partEl))
                return;

            if (partEl.ValueKind != JsonValueKind.Object)
                return;

            // Extract messageID (uppercase D) from the part object
            if (!partEl.TryGetProperty("messageID", out var messageIdEl))
                return;

            var messageId = messageIdEl.GetString();
            if (string.IsNullOrEmpty(messageId))
                return;

            // Extract the "type" discriminator from raw JSON before deserializing.
            // STJ [JsonPolymorphic] requires the discriminator to be the FIRST property in the
            // JSON object, but real OpenCode SSE payloads have "messageID" before "type".
            // Avoid the JsonException by extracting "type" manually and dispatching to the
            // concrete subtype — the same pattern TryPersistMessageAsync uses for "role".
            if (!partEl.TryGetProperty("type", out var typeEl))
                return;

            OpenCodeMessagePart? openCodePart = typeEl.GetString() switch
            {
                "text" => partEl.Deserialize<OpenCodeTextPart>(OpenCodeJsonOptions.Default),
                "tool" => partEl.Deserialize<OpenCodeToolPart>(OpenCodeJsonOptions.Default),
                "reasoning" => partEl.Deserialize<OpenCodeReasoningPart>(OpenCodeJsonOptions.Default),
                _ => null,
            };

            if (openCodePart is null)
                return; // Unsupported or unknown part type

            // Map to Fleet MessagePart
            var fleetPart = OpenCodeMapper.MapPart(openCodePart);
            if (fleetPart is null)
                return; // Mapper returned null (e.g. text part with null Text)

            using var scope = _scopeFactory.CreateScope();
            var messageRepo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();

            var existing = await messageRepo.GetByIdAsync(messageId, fleetSessionId).ConfigureAwait(false);

            PersistedMessage persisted;
            if (existing is null)
            {
                // Create new skeleton message for this assistant part
                var partsJson = JsonSerializer.Serialize(
                    new[] { fleetPart }, MessagePersistenceService.SerializerOptions);
                persisted = new PersistedMessage
                {
                    Id = messageId,
                    SessionId = fleetSessionId,
                    Role = "assistant",
                    PartsJson = partsJson,
                    Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                };
            }
            else
            {
                persisted = MessagePersistenceService.MergePart(existing, fleetPart);
            }

            await messageRepo.UpsertAsync(persisted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Silent failure — persistence must never crash event relay
            LogPersistFailed(_logger, fleetSessionId, ex);
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
