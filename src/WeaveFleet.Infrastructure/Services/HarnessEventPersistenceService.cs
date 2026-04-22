using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Handles durable persistence of harness events (message lifecycle, session lifecycle).
/// Text-delta buffering lives in the shared <see cref="TextDeltaBuffer"/> singleton so buffered
/// fragments survive across scoped projection invocations and disconnect flushes.
/// </summary>
public sealed class HarnessEventPersistenceService : IHarnessEventPersister
{
    private readonly IMessageRepository _messageRepository;
    private readonly ISessionRepository _sessionRepository;
    private readonly SessionActivityWriteService _sessionActivityWriteService;
    private readonly TextDeltaBuffer _deltaBuffer;
    private readonly string? _legacyOwnerUserId;

    public HarnessEventPersistenceService(
        IMessageRepository messageRepository,
        ISessionRepository sessionRepository,
        SessionActivityWriteService sessionActivityWriteService,
        TextDeltaBuffer deltaBuffer)
    {
        _messageRepository = messageRepository;
        _sessionRepository = sessionRepository;
        _sessionActivityWriteService = sessionActivityWriteService;
        _deltaBuffer = deltaBuffer;
    }

    /// <summary>
    /// Legacy test-only constructor. Older tests build the service standalone with a baked-in
    /// owner user id and drive it through <see cref="TryHandleDurableEventAsync"/>; the
    /// production path uses the four-arg ctor and <see cref="HandleAsync"/>.
    /// </summary>
    public HarnessEventPersistenceService(
        IMessageRepository messageRepository,
        ISessionRepository sessionRepository,
        SessionActivityWriteService sessionActivityWriteService,
        string ownerUserId)
        : this(messageRepository, sessionRepository, sessionActivityWriteService, new TextDeltaBuffer())
    {
        _legacyOwnerUserId = ownerUserId;
    }

    /// <summary>
    /// Legacy overload — equivalent to <see cref="HandleAsync"/> using the owner id passed to
    /// the test-only constructor. Returns whether the event was classified as durable.
    /// </summary>
    public async Task<bool> TryHandleDurableEventAsync(string fleetSessionId, HarnessEvent evt)
    {
        if (_legacyOwnerUserId is null)
            throw new InvalidOperationException(
                "TryHandleDurableEventAsync is only available on the legacy test-only constructor. " +
                "Use HandleAsync(fleetSessionId, ownerUserId, evt, ct) from the production code path.");
        if (!EventTypeMetadata.Classify(evt.Type).IsDurable) return false;
        await HandleAsync(fleetSessionId, _legacyOwnerUserId, evt, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Legacy overload — buffers via the shared <see cref="TextDeltaBuffer"/>.
    /// </summary>
    public Task FlushBufferedDeltasAsync(string fleetSessionId)
    {
        if (_legacyOwnerUserId is null)
            throw new InvalidOperationException(
                "FlushBufferedDeltasAsync(sid) is only available on the legacy test-only constructor. " +
                "Use FlushBufferedDeltasAsync(sid, ownerUserId, ct) from the production code path.");
        return FlushBufferedDeltasAsync(fleetSessionId, _legacyOwnerUserId, CancellationToken.None);
    }

    /// <summary>
    /// Persist a durable event. No-op for events not classified as durable.
    /// </summary>
    public async Task HandleAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt, CancellationToken ct)
    {
        if (!EventTypeMetadata.Classify(evt.Type).IsDurable)
            return;

        switch (evt.Type)
        {
            case EventTypes.MessageCreated:
            case EventTypes.MessageUpdated:
                await TryPersistMessageAsync(fleetSessionId, ownerUserId, evt).ConfigureAwait(false);
                return;
            case EventTypes.MessagePartUpdated:
                await TryPersistPartAsync(fleetSessionId, ownerUserId, evt).ConfigureAwait(false);
                return;
            case EventTypes.MessageRemoved:
                await TryHandleMessageRemovedAsync(fleetSessionId, ownerUserId, evt).ConfigureAwait(false);
                return;
            case EventTypes.MessagePartRemoved:
                await TryHandleMessagePartRemovedAsync(fleetSessionId, ownerUserId, evt).ConfigureAwait(false);
                return;
            case EventTypes.SessionUpdated:
                await TryHandleSessionUpdatedAsync(fleetSessionId, ownerUserId, evt).ConfigureAwait(false);
                return;
            case EventTypes.SessionError:
            case EventTypes.SessionCompacted:
            case EventTypes.SessionDeleted:
                await EmitOutboxOnlyAsync(fleetSessionId, ownerUserId, evt).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Buffers a text delta from a <c>message.part.delta</c> event. Called by the ephemeral
    /// relay service (which sees every delta over core NATS) so the buffer is populated by the
    /// time the corresponding <c>message.updated</c> arrives on the durable path.
    /// </summary>
    public void BufferTextDelta(string fleetSessionId, HarnessEvent evt)
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

        _deltaBuffer.Append(fleetSessionId, messageId, partId, delta);
    }

    /// <summary>
    /// Flushes all buffered text deltas to the database. Called when a harness disconnects
    /// to ensure partial streaming content is not lost.
    /// </summary>
    public async Task FlushBufferedDeltasAsync(string fleetSessionId, string ownerUserId, CancellationToken ct)
    {
        var entries = _deltaBuffer.SnapshotSession(fleetSessionId);
        if (entries.Count == 0) return;

        var byMessage = entries.GroupBy(kv => kv.Key.MessageId);

        foreach (var group in byMessage)
        {
            var messageId = group.Key;
            try
            {
                using var userScope = BackgroundUserContext.BeginScope(ownerUserId);
                var existing = await _messageRepository.GetByIdAsync(messageId, fleetSessionId).ConfigureAwait(false);
                if (existing is null)
                    continue;

                var merged = existing;
                foreach (var entry in group)
                {
                    merged = MessagePersistenceService.MergeTextDeltaAndMetadata(merged, entry.Value, merged.Role, merged.AgentName);
                }
                _deltaBuffer.ClearMessage(fleetSessionId, messageId);

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
                                UserId = ownerUserId,
                                CreatedAt = createdAt,
                                AvailableAt = createdAt
                            }
                        ]
                    },
                    ct).ConfigureAwait(false);
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

    private async Task<bool> TryPersistMessageAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt)
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
                ? OpenCodeMessageDeserializer.DeserializeAssistantMessage(infoEl)
                : OpenCodeMessageDeserializer.DeserializeUserMessage(infoEl);
            if (info is null) return false;

            IReadOnlyList<OpenCodeMessagePart> parts = [];
            if (payload.TryGetProperty("parts", out var partsEl))
                parts = OpenCodeHttpClient.DeserializeParts(partsEl);

            var openCodeMessage = new OpenCodeMessageWithParts { Info = info, Parts = parts };
            var harnessMessage = OpenCodeMapper.ToHarnessMessage(openCodeMessage);

            using var userScope = BackgroundUserContext.BeginScope(ownerUserId);
            var persisted = MessagePersistenceService.ToPersistedMessage(fleetSessionId, harnessMessage);
            var existing = await _messageRepository.GetByIdAsync(persisted.Id, persisted.SessionId).ConfigureAwait(false);

            var modelIdChanged = existing is not null && existing.ModelId is null && persisted.ModelId is not null;
            if (modelIdChanged)
                existing!.ModelId = persisted.ModelId;

            if (harnessMessage.Parts.Count == 0)
            {
                if (existing is not null)
                {
                    var merged = MessagePersistenceService.MergeMetadata(existing, persisted.Role, persisted.AgentName);
                    if (existing.Role != merged.Role || existing.AgentName != merged.AgentName || modelIdChanged)
                    {
                        await WriteDurableEventAsync(fleetSessionId, ownerUserId, evt, merged).ConfigureAwait(false);
                        return true;
                    }
                    return false;
                }
            }

            if (evt.Type == EventTypes.MessageUpdated)
            {
                var base_ = existing ?? persisted;
                var mergedBase = existing is null
                    ? persisted
                    : MessagePersistenceService.MergeTimestampAndMetadata(
                        existing,
                        persisted.Timestamp,
                        persisted.Role,
                        persisted.AgentName,
                        persisted.ModelId);
                var merged = ApplyBufferedTextDeltaIfPresent(fleetSessionId, mergedBase, persisted.Id, persisted.Role, persisted.AgentName);
                if (existing is null
                    || existing.Role != merged.Role
                    || existing.AgentName != merged.AgentName
                    || existing.PartsJson != merged.PartsJson
                    || existing.Timestamp != merged.Timestamp
                    || modelIdChanged)
                {
                    await WriteDurableEventAsync(fleetSessionId, ownerUserId, evt, merged).ConfigureAwait(false);
                    return true;
                }
                return false;
            }

            await WriteDurableEventAsync(fleetSessionId, ownerUserId, evt, persisted).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> TryPersistPartAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt)
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

            using var userScope = BackgroundUserContext.BeginScope(ownerUserId);
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

            persisted = MergeBufferedDeltaIntoPartIfLonger(fleetSessionId, messageId, openCodePart.Id, persisted);
            _deltaBuffer.ClearPart(fleetSessionId, messageId, openCodePart.Id);
            await WriteDurableEventAsync(fleetSessionId, ownerUserId, evt, persisted).ConfigureAwait(false);
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

    private async Task<bool> TryHandleMessageRemovedAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt)
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

            using var userScope = BackgroundUserContext.BeginScope(ownerUserId);
            await _messageRepository.DeleteByIdAsync(messageId, fleetSessionId).ConfigureAwait(false);

            await EmitOutboxEventAsync(fleetSessionId, ownerUserId, evt).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> TryHandleMessagePartRemovedAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt)
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

            using var userScope = BackgroundUserContext.BeginScope(ownerUserId);
            await _messageRepository.RemovePartAsync(messageId, fleetSessionId, partId).ConfigureAwait(false);

            await EmitOutboxEventAsync(fleetSessionId, ownerUserId, evt).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> TryHandleSessionUpdatedAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt)
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

            using var userScope = BackgroundUserContext.BeginScope(ownerUserId);
            await _sessionRepository.UpdateTitleAsync(fleetSessionId, title).ConfigureAwait(false);

            await EmitOutboxEventAsync(fleetSessionId, ownerUserId, evt).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task EmitOutboxOnlyAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt)
    {
        try
        {
            using var userScope = BackgroundUserContext.BeginScope(ownerUserId);
            await EmitOutboxEventAsync(fleetSessionId, ownerUserId, evt).ConfigureAwait(false);
        }
        catch (Exception) { /* swallow — matches original best-effort behaviour */ }
    }

    // -----------------------------------------------------------------------
    // Private: helpers
    // -----------------------------------------------------------------------

    private async Task EmitOutboxEventAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt)
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
                        UserId = ownerUserId,
                        CreatedAt = createdAt,
                        AvailableAt = createdAt
                    }
                ]
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WriteDurableEventAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt, PersistedMessage persistedMessage)
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
                            UserId = ownerUserId,
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
        var matchingEntries = _deltaBuffer.SnapshotSession(fleetSessionId)
            .Where(kv => kv.Key.MessageId == messageId)
            .ToArray();

        if (matchingEntries.Length == 0)
            return MessagePersistenceService.MergeMetadata(existing, role, agentName);

        var merged = existing;
        foreach (var entry in matchingEntries)
        {
            merged = MessagePersistenceService.MergeTextDeltaAndMetadata(merged, entry.Value, role, agentName);
        }
        _deltaBuffer.ClearMessage(fleetSessionId, messageId);
        return merged;
    }

    private PersistedMessage MergeBufferedDeltaIntoPartIfLonger(
        string fleetSessionId,
        string messageId,
        string partId,
        PersistedMessage persisted)
    {
        var session = _deltaBuffer.SnapshotSession(fleetSessionId);
        if (!session.TryGetValue((messageId, partId), out var bufferedText) || string.IsNullOrEmpty(bufferedText))
            return persisted;

        var parts = JsonSerializer.Deserialize<List<MessagePart>>(
            persisted.PartsJson, MessagePersistenceService.SerializerOptions) ?? [];
        var idx = parts.FindIndex(p => p is TextPart);
        if (idx < 0)
            return persisted;

        var snapshotText = ((TextPart)parts[idx]).Text;
        if (bufferedText.Length <= snapshotText.Length)
            return persisted;

        parts[idx] = new TextPart(bufferedText);
        return new PersistedMessage
        {
            Id = persisted.Id,
            SessionId = persisted.SessionId,
            Role = persisted.Role,
            PartsJson = JsonSerializer.Serialize(parts, MessagePersistenceService.SerializerOptions),
            Timestamp = persisted.Timestamp,
            CreatedAt = persisted.CreatedAt,
            AgentName = persisted.AgentName,
            ModelId = persisted.ModelId,
        };
    }

}
