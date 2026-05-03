using NuCode.Events;

namespace NuCode.Sessions;

/// <summary>
/// Orchestrates session lifecycle over the persistence store and event bus.
/// All mutations publish corresponding events.
/// </summary>
internal sealed class SessionService : ISessionService
{
    private readonly ISessionStore _store;
    private readonly INuCodeEventBus _eventBus;

    /// <summary>Library version stamped on new sessions.</summary>
    private const string LibraryVersion = "0.1.0";

    public SessionService(ISessionStore store, INuCodeEventBus eventBus)
    {
        _store = store;
        _eventBus = eventBus;
    }

    // ── Session lifecycle ──

    public async Task<NuCodeSession> CreateSessionAsync(string directory, string? title, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var id = SessionId.New();
        var session = new NuCodeSession
        {
            Id = id,
            Slug = GenerateSlug(id),
            Directory = directory,
            Title = title ?? $"New session - {now:O}",
            Version = LibraryVersion,
            CreatedAt = now,
            UpdatedAt = now,
        };

        session = await _store.CreateSessionAsync(session, ct);
        PublishSessionCreated(session);
        return session;
    }

    public async Task<NuCodeSession> CreateChildSessionAsync(
        SessionId parentId, string directory, string? title, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var id = SessionId.New();
        var session = new NuCodeSession
        {
            Id = id,
            Slug = GenerateSlug(id),
            Directory = directory,
            Title = title ?? $"New session - {now:O}",
            Version = LibraryVersion,
            ParentId = parentId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        session = await _store.CreateSessionAsync(session, ct);
        PublishSessionCreated(session);
        return session;
    }

    public Task<NuCodeSession?> GetSessionAsync(SessionId id, CancellationToken ct)
        => _store.GetSessionAsync(id, ct);

    public Task<IReadOnlyList<NuCodeSession>> ListSessionsAsync(SessionFilter filter, CancellationToken ct)
        => _store.ListSessionsAsync(filter, ct);

    public async Task<NuCodeSession> SetTitleAsync(SessionId id, string title, CancellationToken ct)
    {
        var session = await GetRequiredSessionAsync(id, ct);
        var updated = session with { Title = title, UpdatedAt = DateTimeOffset.UtcNow };
        updated = await _store.UpdateSessionAsync(updated, ct);
        PublishSessionUpdated(updated);
        return updated;
    }

    public async Task<NuCodeSession> ArchiveSessionAsync(SessionId id, CancellationToken ct)
    {
        var session = await GetRequiredSessionAsync(id, ct);
        var updated = session with { ArchivedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        updated = await _store.UpdateSessionAsync(updated, ct);
        PublishSessionUpdated(updated);
        return updated;
    }

    public async Task<NuCodeSession> UnarchiveSessionAsync(SessionId id, CancellationToken ct)
    {
        var session = await GetRequiredSessionAsync(id, ct);
        var updated = session with { ArchivedAt = null, UpdatedAt = DateTimeOffset.UtcNow };
        updated = await _store.UpdateSessionAsync(updated, ct);
        PublishSessionUpdated(updated);
        return updated;
    }

    public async Task<NuCodeSession> SetPermissionsAsync(
        SessionId id, Permissions.PermissionRuleset ruleset, CancellationToken ct)
    {
        var session = await GetRequiredSessionAsync(id, ct);
        var updated = session with { Permissions = ruleset, UpdatedAt = DateTimeOffset.UtcNow };
        updated = await _store.UpdateSessionAsync(updated, ct);
        PublishSessionUpdated(updated);
        return updated;
    }

    public async Task<NuCodeSession> SetSummaryAsync(SessionId id, SessionSummary summary, CancellationToken ct)
    {
        var session = await GetRequiredSessionAsync(id, ct);
        var updated = session with { Summary = summary, UpdatedAt = DateTimeOffset.UtcNow };
        updated = await _store.UpdateSessionAsync(updated, ct);
        PublishSessionUpdated(updated);
        return updated;
    }

    public async Task<NuCodeSession> SetRevertAsync(SessionId id, SessionRevert revert, CancellationToken ct)
    {
        var session = await GetRequiredSessionAsync(id, ct);
        var updated = session with { Revert = revert, UpdatedAt = DateTimeOffset.UtcNow };
        updated = await _store.UpdateSessionAsync(updated, ct);
        PublishSessionUpdated(updated);
        return updated;
    }

    public async Task<NuCodeSession> ClearRevertAsync(SessionId id, CancellationToken ct)
    {
        var session = await GetRequiredSessionAsync(id, ct);
        var updated = session with { Revert = null, UpdatedAt = DateTimeOffset.UtcNow };
        updated = await _store.UpdateSessionAsync(updated, ct);
        PublishSessionUpdated(updated);
        return updated;
    }

    public async Task<NuCodeSession> TouchSessionAsync(SessionId id, CancellationToken ct)
    {
        var session = await GetRequiredSessionAsync(id, ct);
        var updated = session with { UpdatedAt = DateTimeOffset.UtcNow };
        updated = await _store.UpdateSessionAsync(updated, ct);
        PublishSessionUpdated(updated);
        return updated;
    }

    public async Task<NuCodeSession> SetCompactingAsync(SessionId id, CancellationToken ct)
    {
        var session = await GetRequiredSessionAsync(id, ct);
        var updated = session with { CompactingAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        updated = await _store.UpdateSessionAsync(updated, ct);
        PublishSessionUpdated(updated);
        return updated;
    }

    public async Task<NuCodeSession> ClearCompactingAsync(SessionId id, CancellationToken ct)
    {
        var session = await GetRequiredSessionAsync(id, ct);
        var updated = session with { CompactingAt = null, UpdatedAt = DateTimeOffset.UtcNow };
        updated = await _store.UpdateSessionAsync(updated, ct);
        PublishSessionUpdated(updated);
        return updated;
    }

    public async Task DeleteSessionAsync(SessionId id, CancellationToken ct)
    {
        var session = await _store.GetSessionAsync(id, ct);
        await _store.DeleteSessionAsync(id, ct);

        if (session is not null)
        {
            _eventBus.Publish(SessionEvents.Deleted, new SessionEvents.SessionInfo(id, session.Title));
        }
    }

    // ── Messages ──

    public async Task<NuCodeMessage> UpsertMessageAsync(NuCodeMessage message, CancellationToken ct)
    {
        var result = await _store.UpsertMessageAsync(message, ct);

        _eventBus.Publish(
            MessageEvents.Updated,
            new MessageEvents.MessageInfo(message.SessionId, message.Id));

        return result;
    }

    public Task<IReadOnlyList<MessageWithParts>> GetMessagesAsync(SessionId sessionId, CancellationToken ct)
        => _store.GetMessagesAsync(sessionId, null, ct);

    public Task<IReadOnlyList<MessageWithParts>> GetMessagesAsync(SessionId sessionId, int limit, CancellationToken ct)
        => _store.GetMessagesAsync(sessionId, limit, ct);

    public async Task DeleteMessageAsync(SessionId sessionId, MessageId messageId, CancellationToken ct)
    {
        await _store.DeleteMessageAsync(sessionId, messageId, ct);

        _eventBus.Publish(
            MessageEvents.Removed,
            new MessageEvents.MessageInfo(sessionId, messageId));
    }

    // ── Parts ──

    public async Task<MessagePart> UpsertPartAsync(MessagePart part, CancellationToken ct)
    {
        var result = await _store.UpsertPartAsync(part, ct);

        _eventBus.Publish(
            MessageEvents.PartUpdated,
            new MessageEvents.PartInfo(part.SessionId, part.MessageId, part.Id));

        return result;
    }

    public void PublishPartDelta(
        SessionId sessionId, MessageId messageId, PartId partId, string field, string delta)
    {
        _eventBus.Publish(
            MessageEvents.PartDeltaReceived,
            new MessageEvents.PartDelta(sessionId, messageId, partId, field, delta));
    }

    public async Task DeletePartAsync(
        SessionId sessionId, MessageId messageId, PartId partId, CancellationToken ct)
    {
        await _store.DeletePartAsync(sessionId, messageId, partId, ct);

        _eventBus.Publish(
            MessageEvents.PartRemoved,
            new MessageEvents.PartInfo(sessionId, messageId, partId));
    }

    // ── Helpers ──

    private async Task<NuCodeSession> GetRequiredSessionAsync(SessionId id, CancellationToken ct)
    {
        return await _store.GetSessionAsync(id, ct)
            ?? throw new InvalidOperationException($"Session '{id}' not found.");
    }

    private void PublishSessionCreated(NuCodeSession session)
    {
        _eventBus.Publish(
            SessionEvents.Created,
            new SessionEvents.SessionInfo(session.Id, session.Title));
    }

    private void PublishSessionUpdated(NuCodeSession session)
    {
        _eventBus.Publish(
            SessionEvents.Updated,
            new SessionEvents.SessionInfo(session.Id, session.Title));
    }

    private static string GenerateSlug(SessionId id)
    {
        // Use first 8 chars of the ULID for a compact slug
        var value = id.Value;
        return value.Length > 8 ? value[..8].ToLowerInvariant() : value.ToLowerInvariant();
    }
}
