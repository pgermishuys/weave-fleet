using System.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemoryMessageRepository : IMessageRepository
{
    // Composite key: (id, sessionId)
    private readonly Dictionary<(string Id, string SessionId), PersistedMessage> _store = new();

    // ── Seeding API ──────────────────────────────────────────────────────────

    public void Seed(PersistedMessage message) => _store[(message.Id, message.SessionId)] = message;

    public void Seed(params PersistedMessage[] messages)
    {
        foreach (var m in messages)
            Seed(m);
    }

    // ── Inspection API ───────────────────────────────────────────────────────

    public IReadOnlyList<PersistedMessage> All => [.. _store.Values];

    // ── Call tracking & configurable behaviors ───────────────────────────────

    public List<(string SessionId, int Limit, string? BeforeMessageId)> GetBySessionCalls { get; } = [];
    public List<PersistedMessage> UpsertCalls { get; } = [];

    /// <summary>
    /// Optional override for <see cref="GetBySessionAsync"/>. When set, this function is called instead of the in-memory store.
    /// Supports dynamic return values based on arguments (e.g., returning limit+1 rows to trigger hasMore).
    /// </summary>
    public Func<string, int, string?, Task<IReadOnlyList<PersistedMessage>>>? GetBySessionBehavior { get; set; }

    /// <summary>
    /// Optional override for <see cref="GetByIdAsync"/>. When set, called instead of the in-memory store.
    /// Supports dynamic return values (e.g., returning null on first call, then a message).
    /// </summary>
    public Func<string, string, Task<PersistedMessage?>>? GetByIdBehavior { get; set; }

    /// <summary>
    /// Optional override for <see cref="UpsertAsync(PersistedMessage)"/>. When set, called after recording to UpsertCalls.
    /// Supports TaskCompletionSource signaling and argument capture.
    /// </summary>
    public Func<PersistedMessage, Task>? UpsertBehavior { get; set; }

    // ── IMessageRepository ───────────────────────────────────────────────────

    public Task UpsertAsync(PersistedMessage message)
    {
        _store[(message.Id, message.SessionId)] = message;
        UpsertCalls.Add(message);
        if (UpsertBehavior is not null)
            return UpsertBehavior(message);
        return Task.CompletedTask;
    }

    public Task UpsertAsync(IDbConnection connection, IDbTransaction? transaction, PersistedMessage message)
        => UpsertAsync(message);

    public Task UpsertBatchAsync(IReadOnlyList<PersistedMessage> messages)
    {
        foreach (var m in messages)
            _store[(m.Id, m.SessionId)] = m;
        return Task.CompletedTask;
    }

    public Task UpsertBatchAsync(IDbConnection connection, IDbTransaction transaction, IReadOnlyList<PersistedMessage> messages)
        => UpsertBatchAsync(messages);

    public Task<IReadOnlyList<PersistedMessage>> GetBySessionAsync(string sessionId, int limit, string? beforeMessageId)
    {
        GetBySessionCalls.Add((sessionId, limit, beforeMessageId));

        if (GetBySessionBehavior is not null)
            return GetBySessionBehavior(sessionId, limit, beforeMessageId);

        var messages = _store.Values
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Timestamp)
            .AsEnumerable();

        if (beforeMessageId is not null)
        {
            var before = _store.Values.FirstOrDefault(m => m.SessionId == sessionId && m.Id == beforeMessageId);
            if (before is not null)
                messages = messages.Where(m => string.Compare(m.Timestamp, before.Timestamp, StringComparison.Ordinal) < 0);
        }

        IReadOnlyList<PersistedMessage> result = [.. messages.TakeLast(limit)];
        return Task.FromResult(result);
    }

    public Task<int> CountBySessionAsync(string sessionId)
        => Task.FromResult(_store.Values.Count(m => m.SessionId == sessionId));

    public Task<bool> HasMessagesAsync(string sessionId)
        => Task.FromResult(_store.Values.Any(m => m.SessionId == sessionId));

    public Task<PersistedMessage?> GetByIdAsync(string id, string sessionId)
    {
        if (GetByIdBehavior is not null)
            return GetByIdBehavior(id, sessionId);
        return Task.FromResult(_store.GetValueOrDefault((id, sessionId)));
    }

    public Task DeleteBySessionAsync(string sessionId)
    {
        var keys = _store.Keys.Where(k => k.SessionId == sessionId).ToList();
        foreach (var key in keys)
            _store.Remove(key);
        return Task.CompletedTask;
    }

    public Task DeleteByIdAsync(string id, string sessionId)
    {
        _store.Remove((id, sessionId));
        return Task.CompletedTask;
    }

    public Task RemovePartAsync(string messageId, string sessionId, string partId)
    {
        if (!_store.TryGetValue((messageId, sessionId), out var existing))
            return Task.CompletedTask;

        var parts = System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(existing.PartsJson) ?? [];
        var filtered = parts.Where(p =>
        {
            if (p.TryGetProperty("id", out var idEl) && idEl.GetString() == partId)
                return false;
            return true;
        }).ToList();

        var updated = new PersistedMessage
        {
            Id = existing.Id,
            SessionId = existing.SessionId,
            Role = existing.Role,
            PartsJson = System.Text.Json.JsonSerializer.Serialize(filtered),
            Timestamp = existing.Timestamp,
            CreatedAt = existing.CreatedAt,
            AgentName = existing.AgentName,
        };
        _store[(messageId, sessionId)] = updated;
        return Task.CompletedTask;
    }
}
