using System.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemorySessionRepository : ISessionRepository
{
    private readonly Dictionary<string, Session> _store = new();

    // ── Seeding API ──────────────────────────────────────────────────────────

    public void Seed(Session session) => _store[session.Id] = session;

    public void Seed(params Session[] sessions)
    {
        foreach (var s in sessions)
            Seed(s);
    }

    // ── Inspection API ───────────────────────────────────────────────────────

    public IReadOnlyList<Session> All => [.. _store.Values];

    // ── Call tracking ────────────────────────────────────────────────────────

    public List<Session> InsertedSessions { get; } = [];
    public List<(string Id, int Tokens, double Cost)> IncrementTokensAsyncCalls { get; } = [];
    public List<(string Id, string ArchivedAt)> ArchiveCalls { get; } = [];
    public List<string> UnarchiveCalls { get; } = [];
    public List<(string Id, string Status, string? StoppedAt)> UpdateStatusCalls { get; } = [];
    public List<(int Limit, int Offset, IReadOnlyList<string>? Statuses, string? ProjectId, IReadOnlyList<string>? RetentionStatuses)> ListAsyncCalls { get; } = [];

    // ── Configurable behaviors ───────────────────────────────────────────────

    /// <summary>
    /// Optional override for <see cref="GetAnyForInstanceAsync"/>. When set, called instead of the in-memory store.
    /// Supports dynamic return values (e.g., returning null on first N calls, then a session).
    /// </summary>
    public Func<string, Task<Session?>>? GetAnyForInstanceBehavior { get; set; }

    /// <summary>
    /// Optional override for <see cref="GetByHarnessIdAsync"/>. When set, called instead of the in-memory store.
    /// Supports dynamic return values (e.g., returning null until a session is inserted).
    /// </summary>
    public Func<string, Task<Session?>>? GetByHarnessIdBehavior { get; set; }

    // ── ISessionRepository ───────────────────────────────────────────────────

    public Task InsertAsync(Session session)
    {
        _store[session.Id] = session;
        InsertedSessions.Add(session);
        return Task.CompletedTask;
    }

    public Task InsertAsync(IDbConnection connection, IDbTransaction? transaction, Session session)
        => InsertAsync(session);

    public Task<Session?> GetByIdAsync(string id)
        => Task.FromResult(_store.GetValueOrDefault(id));

    public Task<Session?> GetByHarnessIdAsync(string harnessSessionId)
    {
        if (GetByHarnessIdBehavior is not null)
            return GetByHarnessIdBehavior(harnessSessionId);
        return Task.FromResult(_store.Values.FirstOrDefault(s => s.OpencodeSessionId == harnessSessionId));
    }

    public Task<IReadOnlyList<Session>> ListAsync(int limit = 100, int offset = 0, IReadOnlyList<string>? statuses = null, string? projectId = null)
    {
        var query = _store.Values.AsEnumerable();
        if (statuses is { Count: > 0 })
            query = query.Where(s => statuses.Contains(s.Status));
        if (projectId is not null)
            query = query.Where(s => s.ProjectId == projectId);
        IReadOnlyList<Session> result = [.. query.Skip(offset).Take(limit)];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Session>> ListAsync(int limit, int offset, IReadOnlyList<string>? statuses, string? projectId, IReadOnlyList<string>? retentionStatuses)
    {
        ListAsyncCalls.Add((limit, offset, statuses, projectId, retentionStatuses));
        var query = _store.Values.AsEnumerable();
        if (statuses is { Count: > 0 })
            query = query.Where(s => statuses.Contains(s.Status));
        if (projectId is not null)
            query = query.Where(s => s.ProjectId == projectId);
        if (retentionStatuses is { Count: > 0 })
            query = query.Where(s => retentionStatuses.Contains(s.RetentionStatus));
        query = query.Where(s => s.ParentSessionId is null);
        IReadOnlyList<Session> result = [.. query.Skip(offset).Take(limit)];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Session>> ListAsync(int limit, int offset, IReadOnlyList<string>? statuses, string? projectId, IReadOnlyList<string>? retentionStatuses, string viewMode)
    {
        var query = _store.Values.AsEnumerable().Where(s => s.ViewMode == viewMode);
        if (statuses is { Count: > 0 })
            query = query.Where(s => statuses.Contains(s.Status));
        if (projectId is not null)
            query = query.Where(s => s.ProjectId == projectId);
        if (retentionStatuses is { Count: > 0 })
            query = query.Where(s => retentionStatuses.Contains(s.RetentionStatus));
        query = query.Where(s => s.ParentSessionId is null);
        IReadOnlyList<Session> result = [.. query.Skip(offset).Take(limit)];
        return Task.FromResult(result);
    }

    public Task DeleteByProjectIdAsync(string projectId)
    {
        var ids = _store.Values.Where(s => s.ProjectId == projectId).Select(s => s.Id).ToList();
        foreach (var id in ids)
            _store.Remove(id);
        return Task.CompletedTask;
    }

    public Task<int> CountAsync(IReadOnlyList<string>? statuses = null)
    {
        var query = _store.Values.AsEnumerable();
        if (statuses is { Count: > 0 })
            query = query.Where(s => statuses.Contains(s.Status));
        return Task.FromResult(query.Count());
    }

    public Task<int> CountAsync(IReadOnlyList<string>? statuses, IReadOnlyList<string>? retentionStatuses)
    {
        var query = _store.Values.AsEnumerable();
        if (statuses is { Count: > 0 })
            query = query.Where(s => statuses.Contains(s.Status));
        if (retentionStatuses is { Count: > 0 })
            query = query.Where(s => retentionStatuses.Contains(s.RetentionStatus));
        return Task.FromResult(query.Count());
    }

    public Task<int> CountAsync(IReadOnlyList<string>? statuses, IReadOnlyList<string>? retentionStatuses, string viewMode)
    {
        var query = _store.Values.AsEnumerable().Where(s => s.ViewMode == viewMode);
        if (statuses is { Count: > 0 })
            query = query.Where(s => statuses.Contains(s.Status));
        if (retentionStatuses is { Count: > 0 })
            query = query.Where(s => retentionStatuses.Contains(s.RetentionStatus));
        return Task.FromResult(query.Count());
    }

    public Task<(int Active, int Idle)> GetStatusCountsAsync()
    {
        var active = _store.Values.Count(s => s.Status == "active");
        var idle = _store.Values.Count(s => s.Status == "idle");
        return Task.FromResult((active, idle));
    }

    public Task<(int Active, int Idle)> GetStatusCountsAsync(string viewMode)
    {
        var query = _store.Values.Where(s => s.ViewMode == viewMode);
        var active = query.Count(s => s.Status == "active");
        var idle = query.Count(s => s.Status == "idle");
        return Task.FromResult((active, idle));
    }

    public Task<IReadOnlyList<Session>> ListActiveAsync()
    {
        IReadOnlyList<Session> result = [.. _store.Values.Where(s => s.Status == "active")];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Session>> ListActiveAsync(IReadOnlyList<string>? retentionStatuses)
    {
        var query = _store.Values.Where(s => s.Status == "active");
        if (retentionStatuses is { Count: > 0 })
            query = query.Where(s => retentionStatuses.Contains(s.RetentionStatus));
        IReadOnlyList<Session> result = [.. query];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Session>> ListActiveAsync(IReadOnlyList<string>? retentionStatuses, string viewMode)
    {
        var query = _store.Values.Where(s => s.Status == "active" && s.ViewMode == viewMode);
        if (retentionStatuses is { Count: > 0 })
            query = query.Where(s => retentionStatuses.Contains(s.RetentionStatus));
        IReadOnlyList<Session> result = [.. query];
        return Task.FromResult(result);
    }

    public Task UpdateStatusAsync(string id, string status, string? stoppedAt = null)
    {
        UpdateStatusCalls.Add((id, status, stoppedAt));
        if (_store.TryGetValue(id, out var session))
        {
            session.Status = status;
            if (stoppedAt is not null)
                session.StoppedAt = stoppedAt;
        }
        return Task.CompletedTask;
    }

    public Task UpdateStatusAsync(IDbConnection connection, IDbTransaction? transaction, string id, string status, string? stoppedAt)
        => UpdateStatusAsync(id, status, stoppedAt);

    public Task ArchiveAsync(string id, string archivedAt)
    {
        ArchiveCalls.Add((id, archivedAt));
        if (_store.TryGetValue(id, out var session))
        {
            session.RetentionStatus = "archived";
            session.ArchivedAt = archivedAt;
        }
        return Task.CompletedTask;
    }

    public Task ArchiveAsync(IDbConnection connection, IDbTransaction? transaction, string id, string archivedAt)
        => ArchiveAsync(id, archivedAt);

    public Task UnarchiveAsync(string id)
    {
        UnarchiveCalls.Add(id);
        if (_store.TryGetValue(id, out var session))
        {
            session.RetentionStatus = "active";
            session.ArchivedAt = null;
        }
        return Task.CompletedTask;
    }

    public Task UnarchiveAsync(IDbConnection connection, IDbTransaction? transaction, string id)
        => UnarchiveAsync(id);

    public Task<IReadOnlyList<Session>> GetForInstanceAsync(string instanceId)
    {
        IReadOnlyList<Session> result = [.. _store.Values.Where(s => s.InstanceId == instanceId)];
        return Task.FromResult(result);
    }

    public Task<Session?> GetAnyForInstanceAsync(string instanceId)
    {
        if (GetAnyForInstanceBehavior is not null)
            return GetAnyForInstanceBehavior(instanceId);
        return Task.FromResult(_store.Values.FirstOrDefault(s => s.InstanceId == instanceId));
    }

    public Task<IReadOnlyList<Session>> GetNonTerminalForInstanceAsync(string instanceId)
    {
        var terminal = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "stopped", "error" };
        IReadOnlyList<Session> result = [.. _store.Values.Where(s => s.InstanceId == instanceId && !terminal.Contains(s.Status))];
        return Task.FromResult(result);
    }

    public Task UpdateTitleAsync(string id, string title)
    {
        if (_store.TryGetValue(id, out var session))
            session.Title = title;
        return Task.CompletedTask;
    }

    public Task UpdateForResumeAsync(string id, string instanceId)
    {
        if (_store.TryGetValue(id, out var session))
            session.InstanceId = instanceId;
        return Task.CompletedTask;
    }

    public Task UpdateResumeTokenAsync(string id, string resumeToken)
    {
        if (_store.TryGetValue(id, out var session))
            session.HarnessResumeToken = resumeToken;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Session>> GetActiveChildrenAsync(string parentDbId)
    {
        IReadOnlyList<Session> result = [.. _store.Values.Where(s => s.ParentSessionId == parentDbId && s.Status == "active")];
        return Task.FromResult(result);
    }

    public Task<IReadOnlySet<string>> GetIdsWithActiveChildrenAsync()
    {
        var parentIds = _store.Values
            .Where(s => s.ParentSessionId is not null && s.Status == "active")
            .Select(s => s.ParentSessionId!)
            .ToHashSet();
        return Task.FromResult<IReadOnlySet<string>>(parentIds);
    }

    public Task<IReadOnlyList<Session>> GetForWorkspaceAsync(string workspaceId)
    {
        IReadOnlyList<Session> result = [.. _store.Values.Where(s => s.WorkspaceId == workspaceId)];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Session>> GetForWorkspaceAsync(string workspaceId, IReadOnlyList<string>? retentionStatuses)
    {
        var query = _store.Values.Where(s => s.WorkspaceId == workspaceId);
        if (retentionStatuses is { Count: > 0 })
            query = query.Where(s => retentionStatuses.Contains(s.RetentionStatus));
        IReadOnlyList<Session> result = [.. query];
        return Task.FromResult(result);
    }

    public Task<bool> DeleteAsync(string id)
    {
        var removed = _store.Remove(id);
        return Task.FromResult(removed);
    }

    public Task<bool> DeleteAsync(IDbConnection connection, IDbTransaction? transaction, string id)
        => DeleteAsync(id);

    public Task<(int TotalTokens, double TotalCost)?> IncrementTokensAsync(string id, int tokens, double cost)
    {
        IncrementTokensAsyncCalls.Add((id, tokens, cost));
        if (!_store.TryGetValue(id, out var session))
            return Task.FromResult<(int, double)?>(null);

        session.TotalTokens += tokens;
        session.TotalCost += cost;
        return Task.FromResult<(int, double)?>((session.TotalTokens, session.TotalCost));
    }

    public Task<(int TotalTokens, double TotalCost)> GetFleetTokenTotalsAsync()
    {
        var totalTokens = _store.Values.Sum(s => s.TotalTokens);
        var totalCost = _store.Values.Sum(s => s.TotalCost);
        return Task.FromResult((totalTokens, totalCost));
    }

    public Task<int> MarkAllNonTerminalStoppedAsync(string stoppedAt)
    {
        var terminal = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "stopped", "error" };
        var count = 0;
        foreach (var session in _store.Values.Where(s => !terminal.Contains(s.Status)))
        {
            session.Status = "stopped";
            session.StoppedAt = stoppedAt;
            count++;
        }
        return Task.FromResult(count);
    }

    public Task UpdateProjectAsync(string id, string? projectId)
    {
        if (_store.TryGetValue(id, out var session))
            session.ProjectId = projectId;
        return Task.CompletedTask;
    }

    public Task UpdateSelectedModelAsync(string id, string providerId, string modelId)
    {
        if (_store.TryGetValue(id, out var session))
        {
            session.SelectedProviderId = providerId;
            session.SelectedModelId = modelId;
        }
        return Task.CompletedTask;
    }
}
