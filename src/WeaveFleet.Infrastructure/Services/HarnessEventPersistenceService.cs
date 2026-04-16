using System.Collections.Concurrent;
using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Handles durable persistence of harness events (message lifecycle, session lifecycle).
/// Owns the text-delta buffer so partial streaming content is preserved on disconnect.
/// Intended to be created once per harness instance pump lifetime and called from
/// <c>HarnessEventRelay.PumpAsync</c>.
/// </summary>
internal sealed class HarnessEventPersistenceService
{
    private readonly IMessageRepository _messageRepository;
    private readonly ISessionRepository _sessionRepository;
    private readonly SessionActivityWriteService _sessionActivityWriteService;
    private readonly string _ownerUserId;
    private readonly ConcurrentDictionary<(string MessageKey, string PartId), string> _bufferedTextDeltas = new();

    internal HarnessEventPersistenceService(
        IMessageRepository messageRepository,
        ISessionRepository sessionRepository,
        SessionActivityWriteService sessionActivityWriteService,
        string ownerUserId)
    {
        _messageRepository = messageRepository;
        _sessionRepository = sessionRepository;
        _sessionActivityWriteService = sessionActivityWriteService;
        _ownerUserId = ownerUserId;
    }

    /// <summary>
    /// Attempts to handle a durable event. Returns <c>true</c> if the event was handled
    /// durably (persisted + outbox) and should NOT be relayed as ephemeral.
    /// Returns <c>false</c> if the event is ephemeral and should be broadcast directly.
    /// </summary>
    internal async Task<bool> TryHandleDurableEventAsync(string fleetSessionId, HarnessEvent evt)
    {
        if (!EventTypeMetadata.Classify(evt.Type).IsDurable)
            return false;

        return evt.Type switch
        {
            EventTypes.MessageCreated or EventTypes.MessageUpdated =>
                await TryPersistMessageAsync(fleetSessionId, evt).ConfigureAwait(false),
            EventTypes.MessagePartUpdated =>
                await TryPersistPartAsync(fleetSessionId, evt).ConfigureAwait(false),
            EventTypes.MessageRemoved =>
                await TryHandleMessageRemovedAsync(fleetSessionId, evt).ConfigureAwait(false),
            EventTypes.MessagePartRemoved =>
                await TryHandleMessagePartRemovedAsync(fleetSessionId, evt).ConfigureAwait(false),
            EventTypes.SessionUpdated =>
                await TryHandleSessionUpdatedAsync(fleetSessionId, evt).ConfigureAwait(false),
            EventTypes.SessionError =>
                await TryHandleSessionErrorAsync(fleetSessionId, evt).ConfigureAwait(false),
            EventTypes.SessionCompacted =>
                await TryHandleSessionCompactedAsync(fleetSessionId, evt).ConfigureAwait(false),
            EventTypes.SessionDeleted =>
                await TryHandleSessionDeletedAsync(fleetSessionId, evt).ConfigureAwait(false),
            _ => false,
        };
    }

    /// <summary>
    /// Buffers a text delta from a <c>message.part.delta</c> event.
    /// Must be called before <see cref="TryHandleDurableEventAsync"/> for each event.
    /// </summary>
    internal void BufferTextDelta(string fleetSessionId, HarnessEvent evt)
    {
        if (evt.Type != EventTypes.MessagePartDelta || !evt.Payload.HasValue || evt.Payload.Value.ValueKind != JsonValueKind.Object)
            return;

        var payload = evt.Payload.Value;
        if (!payload.TryGetProperty("field", out var fieldEl) || fieldEl.GetString() != "text")
            return;

        if (!payload.TryGetProperty("messageID", out var messageIdEl)
            || !payload.TryGetProperty("partID", out var partIdEl)
            || !payload.TryGetProperty("delta", out var deltaEl))
            return;

        var messageId = messageIdEl.GetString();
        var partId = partIdEl.GetString();
        var delta = deltaEl.GetString();
        if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(partId) || string.IsNullOrEmpty(delta))
            return;

        _bufferedTextDeltas.AddOrUpdate(
            (BuildBufferedDeltaKey(fleetSessionId, messageId, partId), partId),
            delta,
            (_, existing) => existing + delta);
    }

    /// <summary>
    /// Flushes all buffered text deltas to the database. Called when a harness disconnects
    /// to ensure partial streaming content is not lost.
    /// </summary>
    internal async Task FlushBufferedDeltasAsync(string fleetSessionId)
    {
        var prefix = $"{fleetSessionId}::";
        var entries = _bufferedTextDeltas
            .Where(e => e.Key.MessageKey.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();

        if (entries.Length == 0)
            return;

        // Group by message ID (key format: "{fleetSessionId}::{messageId}::{partId}")
        var byMessage = entries
            .GroupBy(e =>
            {
                var key = e.Key.MessageKey;
                var afterPrefix = key[prefix.Length..];
                var sep = afterPrefix.IndexOf("::", StringComparison.Ordinal);
                return sep >= 0 ? afterPrefix[..sep] : afterPrefix;
            });

        foreach (var group in byMessage)
        {
            var messageId = group.Key;
            try
            {
                using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
                var existing = await _messageRepository.GetByIdAsync(messageId, fleetSessionId).ConfigureAwait(false);
                if (existing is null)
                    continue;

                var merged = existing;
                foreach (var entry in group)
                {
                    merged = MessagePersistenceService.MergeTextDeltaAndMetadata(merged, entry.Value, merged.Role, merged.AgentName);
                    _bufferedTextDeltas.TryRemove(entry.Key, out _);
                }

                var createdAt = DateTimeOffset.UtcNow.ToString("O");
                await _sessionActivityWriteService.WriteAsync(
                    new SessionActivityWriteRequest
                    {
                        MessagesToUpsert = [merged],
                        OutboxMessages =
                        [
                            new OutboxMessage
                            {
                                Topic = $"session:{fleetSessionId}",
                                Type = EventTypes.MessageUpdated,
                                Payload = MessagePersistenceService.SerializePayload(
                                    MessagePersistenceService.BuildCommittedMessagePayload(merged)),
                                UserId = _ownerUserId,
                                CreatedAt = createdAt,
                                AvailableAt = createdAt
                            }
                        ]
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort flush — never crash the pump
            }
        }
    }

    // -----------------------------------------------------------------------
    // Private: message lifecycle
    // -----------------------------------------------------------------------

    private async Task<bool> TryPersistMessageAsync(string fleetSessionId, HarnessEvent evt)
    {
        if (evt.Type is not (EventTypes.MessageCreated or EventTypes.MessageUpdated))
            return false;

        try
        {
            if (!evt.Payload.HasValue || evt.Payload.Value.ValueKind != JsonValueKind.Object)
                return false;

            var payload = evt.Payload.Value;

            if (!payload.TryGetProperty("info", out var infoEl))
                return false;

            if (!infoEl.TryGetProperty("role", out var roleEl))
                return false;

            var role = roleEl.GetString();
            if (role is not ("user" or "assistant"))
                return false;

            OpenCodeMessageInfo? info = role == "assistant"
                ? infoEl.Deserialize<OpenCodeAssistantMessage>(OpenCodeJsonOptions.Default)
                : infoEl.Deserialize<OpenCodeUserMessage>(OpenCodeJsonOptions.Default);
            if (info is null) return false;

            IReadOnlyList<OpenCodeMessagePart> parts = [];
            if (payload.TryGetProperty("parts", out var partsEl))
                parts = OpenCodeHttpClient.DeserializeParts(partsEl);

            var openCodeMessage = new OpenCodeMessageWithParts { Info = info, Parts = parts };
            var harnessMessage = OpenCodeMapper.ToHarnessMessage(openCodeMessage);

            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            var persisted = MessagePersistenceService.ToPersistedMessage(fleetSessionId, harnessMessage);
            var existing = await _messageRepository.GetByIdAsync(persisted.Id, persisted.SessionId).ConfigureAwait(false);

            // Track whether the incoming event carries a ModelId that the existing record lacks.
            var modelIdChanged = existing is not null && existing.ModelId is null && persisted.ModelId is not null;

            // Backfill ModelId from the incoming event into any existing record so it
            // is not lost when the skeleton message.created arrives before message.updated.
            if (modelIdChanged)
                existing!.ModelId = persisted.ModelId;

            if (harnessMessage.Parts.Count == 0)
            {
                if (existing is not null)
                {
                    var merged = MessagePersistenceService.MergeMetadata(existing, persisted.Role, persisted.AgentName);
                    if (existing.Role != merged.Role || existing.AgentName != merged.AgentName || modelIdChanged)
                    {
                        await WriteDurableEventAsync(fleetSessionId, evt, merged).ConfigureAwait(false);
                        return true;
                    }
                    return false;
                }
            }

            if (evt.Type == EventTypes.MessageUpdated)
            {
                // Apply any buffered text deltas regardless of whether the message already
                // exists in the DB. A message.updated arriving before any message.part.updated
                // (e.g. after reconnect) must still merge buffered deltas so streaming content
                // is not lost.
                var base_ = existing ?? persisted;
                var merged = ApplyBufferedTextDeltaIfPresent(fleetSessionId, base_, persisted.Id, persisted.Role, persisted.AgentName);
                if (existing is null
                    || existing.Role != merged.Role
                    || existing.AgentName != merged.AgentName
                    || existing.PartsJson != merged.PartsJson
                    || modelIdChanged)
                {
                    await WriteDurableEventAsync(fleetSessionId, evt, merged).ConfigureAwait(false);
                    return true;
                }
                return false;
            }

            await WriteDurableEventAsync(fleetSessionId, evt, persisted).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> TryPersistPartAsync(string fleetSessionId, HarnessEvent evt)
    {
        if (evt.Type is not EventTypes.MessagePartUpdated)
            return false;

        try
        {
            if (!evt.Payload.HasValue || evt.Payload.Value.ValueKind != JsonValueKind.Object)
                return false;

            var payload = evt.Payload.Value;

            if (!payload.TryGetProperty("part", out var partEl) || partEl.ValueKind != JsonValueKind.Object)
                return false;

            if (!partEl.TryGetProperty("messageID", out var messageIdEl))
                return false;

            var messageId = messageIdEl.GetString();
            if (string.IsNullOrEmpty(messageId))
                return false;

            OpenCodeMessagePart? openCodePart = OpenCodePartDeserializer.DeserializePart(partEl);
            if (openCodePart is null)
                return false;

            var fleetPart = OpenCodeMapper.MapPart(openCodePart);
            if (fleetPart is null)
                return false;

            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            var existing = await _messageRepository.GetByIdAsync(messageId, fleetSessionId).ConfigureAwait(false);

            string? agentName = null;
            string? role = null;
            if (payload.TryGetProperty("info", out var infoEl))
            {
                if (infoEl.TryGetProperty("agent", out var agentEl))
                    agentName = agentEl.GetString();
                if (infoEl.TryGetProperty("role", out var roleEl) && roleEl.GetString() is { } rawRole)
                    role = rawRole is "user" or "assistant" ? rawRole : null;
            }

            PersistedMessage persisted;
            if (existing is null)
            {
                var partsJson = fleetPart is ReasoningPart
                    ? "[]"
                    : JsonSerializer.Serialize(new[] { fleetPart }, MessagePersistenceService.SerializerOptions);
                persisted = new PersistedMessage
                {
                    Id = messageId,
                    SessionId = fleetSessionId,
                    Role = role ?? "assistant",
                    PartsJson = partsJson,
                    Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    AgentName = agentName,
                };
            }
            else
            {
                persisted = MessagePersistenceService.MergePartAndMetadata(existing, fleetPart, role, agentName);
            }

            ClearBufferedTextDelta(fleetSessionId, messageId, openCodePart.Id);
            await WriteDurableEventAsync(fleetSessionId, evt, persisted).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Private: new event type handlers
    // -----------------------------------------------------------------------

    private async Task<bool> TryHandleMessageRemovedAsync(string fleetSessionId, HarnessEvent evt)
    {
        try
        {
            if (!evt.Payload.HasValue || evt.Payload.Value.ValueKind != JsonValueKind.Object)
                return false;

            var payload = evt.Payload.Value;
            if (!payload.TryGetProperty("id", out var idEl))
                return false;

            var messageId = idEl.GetString();
            if (string.IsNullOrEmpty(messageId))
                return false;

            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            await _messageRepository.DeleteByIdAsync(messageId, fleetSessionId).ConfigureAwait(false);

            await EmitOutboxEventAsync(fleetSessionId, evt).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> TryHandleMessagePartRemovedAsync(string fleetSessionId, HarnessEvent evt)
    {
        try
        {
            if (!evt.Payload.HasValue || evt.Payload.Value.ValueKind != JsonValueKind.Object)
                return false;

            var payload = evt.Payload.Value;
            if (!payload.TryGetProperty("messageID", out var messageIdEl)
                || !payload.TryGetProperty("id", out var partIdEl))
                return false;

            var messageId = messageIdEl.GetString();
            var partId = partIdEl.GetString();
            if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(partId))
                return false;

            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            await _messageRepository.RemovePartAsync(messageId, fleetSessionId, partId).ConfigureAwait(false);

            await EmitOutboxEventAsync(fleetSessionId, evt).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> TryHandleSessionUpdatedAsync(string fleetSessionId, HarnessEvent evt)
    {
        try
        {
            if (!evt.Payload.HasValue || evt.Payload.Value.ValueKind != JsonValueKind.Object)
                return false;

            var payload = evt.Payload.Value;
            if (!payload.TryGetProperty("title", out var titleEl))
                return false;

            var title = titleEl.GetString();
            if (string.IsNullOrEmpty(title))
                return false;

            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            await _sessionRepository.UpdateTitleAsync(fleetSessionId, title).ConfigureAwait(false);

            await EmitOutboxEventAsync(fleetSessionId, evt).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> TryHandleSessionErrorAsync(string fleetSessionId, HarnessEvent evt)
    {
        try
        {
            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            await EmitOutboxEventAsync(fleetSessionId, evt).ConfigureAwait(false);
            return true; // durable — written to outbox, do not relay directly
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> TryHandleSessionCompactedAsync(string fleetSessionId, HarnessEvent evt)
    {
        try
        {
            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            await EmitOutboxEventAsync(fleetSessionId, evt).ConfigureAwait(false);
            return true; // durable — written to outbox, do not relay directly
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> TryHandleSessionDeletedAsync(string fleetSessionId, HarnessEvent evt)
    {
        try
        {
            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            await EmitOutboxEventAsync(fleetSessionId, evt).ConfigureAwait(false);
            return true; // durable — do not relay
        }
        catch (Exception)
        {
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Private: helpers
    // -----------------------------------------------------------------------

    private async Task EmitOutboxEventAsync(string fleetSessionId, HarnessEvent evt)
    {
        var durablePayload = MessagePersistenceService.SanitizeDurableEventPayload(evt.Type, evt.Payload)
            ?? JsonSerializer.SerializeToElement(new { });
        var createdAt = DateTimeOffset.UtcNow.ToString("O");
        await _sessionActivityWriteService.WriteAsync(
            new SessionActivityWriteRequest
            {
                OutboxMessages =
                [
                    new OutboxMessage
                    {
                        Topic = $"session:{fleetSessionId}",
                        Type = evt.Type,
                        Payload = MessagePersistenceService.SerializePayload(durablePayload),
                        UserId = _ownerUserId,
                        CreatedAt = createdAt,
                        AvailableAt = createdAt
                    }
                ]
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WriteDurableEventAsync(string fleetSessionId, HarnessEvent evt, PersistedMessage persistedMessage)
    {
        var durablePayload = evt.Type == EventTypes.MessageUpdated
            ? MessagePersistenceService.BuildCommittedMessagePayload(persistedMessage)
            : MessagePersistenceService.SanitizeDurableEventPayload(evt.Type, evt.Payload);

        var createdAt = DateTimeOffset.UtcNow.ToString("O");
        await _sessionActivityWriteService.WriteAsync(
            new SessionActivityWriteRequest
            {
                MessagesToUpsert = [persistedMessage],
                OutboxMessages = durablePayload.HasValue
                    ?
                    [
                        new OutboxMessage
                        {
                            Topic = $"session:{fleetSessionId}",
                            Type = evt.Type,
                            Payload = MessagePersistenceService.SerializePayload(durablePayload.Value),
                            UserId = _ownerUserId,
                            CreatedAt = createdAt,
                            AvailableAt = createdAt
                        }
                    ]
                    : []
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private PersistedMessage ApplyBufferedTextDeltaIfPresent(
        string fleetSessionId,
        PersistedMessage existing,
        string messageId,
        string role,
        string? agentName)
    {
        var prefix = BuildBufferedDeltaPrefix(fleetSessionId, messageId);
        var matchingEntries = _bufferedTextDeltas
            .Where(entry => entry.Key.MessageKey.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();

        if (matchingEntries.Length == 0)
            return MessagePersistenceService.MergeMetadata(existing, role, agentName);

        var merged = existing;
        foreach (var entry in matchingEntries)
        {
            merged = MessagePersistenceService.MergeTextDeltaAndMetadata(merged, entry.Value, role, agentName);
            _bufferedTextDeltas.TryRemove(entry.Key, out _);
        }

        return merged;
    }

    private void ClearBufferedTextDelta(string fleetSessionId, string messageId, string partId)
    {
        if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(partId))
            return;

        _bufferedTextDeltas.TryRemove((BuildBufferedDeltaKey(fleetSessionId, messageId, partId), partId), out _);
    }

    private static string BuildBufferedDeltaPrefix(string fleetSessionId, string messageId)
        => $"{fleetSessionId}::{messageId}::";

    private static string BuildBufferedDeltaKey(string fleetSessionId, string messageId, string partId)
        => $"{BuildBufferedDeltaPrefix(fleetSessionId, messageId)}{partId}";
}
