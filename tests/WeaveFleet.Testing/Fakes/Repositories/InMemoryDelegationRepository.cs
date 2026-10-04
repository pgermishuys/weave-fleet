using System.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemoryDelegationRepository : IDelegationRepository
{
    private readonly Dictionary<string, Delegation> _store = new();

    // ── Seeding API ──────────────────────────────────────────────────────────

    public void Seed(Delegation delegation) => _store[delegation.Id] = delegation;

    public void Seed(params Delegation[] delegations)
    {
        foreach (var d in delegations)
            Seed(d);
    }

    // ── Inspection API ───────────────────────────────────────────────────────

    public IReadOnlyList<Delegation> All => [.. _store.Values];

    // ── Call tracking ────────────────────────────────────────────────────────

    public List<Delegation> InsertedDelegations { get; } = [];
    public List<(string Id, string Status, string UpdatedAt, string? CompletedAt)> UpdateStatusCalls { get; } = [];
    public List<(string Id, string? ChildSessionId, string UpdatedAt)> UpdateChildSessionIdCalls { get; } = [];
    public List<string> DeleteByParentSessionIdCalls { get; } = [];

    // ── Configurable behaviors ───────────────────────────────────────────────

    /// <summary>
    /// Optional override for <see cref="GetByParentToolCallIdAsync"/>. When set, called instead of the in-memory store.
    /// Supports dynamic return values (e.g., returning null on first call, then a delegation).
    /// </summary>
    public Func<string, string, Task<Delegation?>>? GetByParentToolCallIdBehavior { get; set; }

    // ── IDelegationRepository ────────────────────────────────────────────────

    public Task InsertAsync(Delegation delegation)
    {
        if (string.IsNullOrEmpty(delegation.WorkId))
            delegation.WorkId = delegation.ParentToolCallId ?? delegation.Id;
        _store[delegation.Id] = delegation;
        InsertedDelegations.Add(delegation);
        return Task.CompletedTask;
    }

    public Task InsertAsync(IDbConnection connection, IDbTransaction? transaction, Delegation delegation)
        => InsertAsync(delegation);

    public Task<Delegation?> GetByIdAsync(string id)
        => Task.FromResult(_store.GetValueOrDefault(id));

    public Task<IReadOnlyList<Delegation>> GetByParentSessionIdAsync(string parentSessionId)
    {
        IReadOnlyList<Delegation> result = [.. _store.Values.Where(d => d.ParentSessionId == parentSessionId)];
        return Task.FromResult(result);
    }

    public Task<Delegation?> GetByChildSessionIdAsync(string childSessionId)
        => Task.FromResult(_store.Values.FirstOrDefault(d => d.ChildSessionId == childSessionId));

    public Task<Delegation?> GetByParentToolCallIdAsync(string parentSessionId, string toolCallId)
    {
        if (GetByParentToolCallIdBehavior is not null)
            return GetByParentToolCallIdBehavior(parentSessionId, toolCallId);
        return Task.FromResult(_store.Values.FirstOrDefault(d => d.ParentSessionId == parentSessionId && d.ParentToolCallId == toolCallId));
    }

    public Task<Delegation?> GetByWorkIdAsync(string parentSessionId, string workId)
        => Task.FromResult(_store.Values.LastOrDefault(d => d.ParentSessionId == parentSessionId && d.WorkId == workId));

    public Task<IReadOnlyList<Delegation>> ListRunningAsync()
    {
        IReadOnlyList<Delegation> result = [.. _store.Values.Where(d => d.IsRunning).OrderBy(d => d.CreatedAt, StringComparer.Ordinal)];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<string, int>> CountRunningAsync(IReadOnlyCollection<string> parentSessionIds)
    {
        IReadOnlyDictionary<string, int> result = _store.Values
            .Where(d => d.IsRunning && parentSessionIds.Contains(d.ParentSessionId))
            .GroupBy(d => d.ParentSessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return Task.FromResult(result);
    }

    public Task UpdateWorkAsync(Delegation delegation)
    {
        _store[delegation.Id] = delegation;
        return Task.CompletedTask;
    }

    public Task EndAsync(string id, string status, string endedReason, string? detail, string endedAt)
    {
        if (_store.TryGetValue(id, out var delegation))
        {
            delegation.Status = status;
            delegation.EndedReason = endedReason;
            delegation.Detail = detail ?? delegation.Detail;
            delegation.UpdatedAt = endedAt;
            delegation.CompletedAt = endedAt;
        }
        return Task.CompletedTask;
    }

    public Task UpdateStatusAsync(string id, string status, string updatedAt, string? completedAt)
    {
        UpdateStatusCalls.Add((id, status, updatedAt, completedAt));
        if (_store.TryGetValue(id, out var delegation))
        {
            delegation.Status = status;
            delegation.UpdatedAt = updatedAt;
            delegation.CompletedAt = completedAt;
            delegation.EndedReason = status is "completed" or "error" or "cancelled" ? delegation.EndedReason ?? status : null;
        }
        return Task.CompletedTask;
    }

    public Task UpdateStatusAsync(IDbConnection connection, IDbTransaction? transaction, string id, string status, string updatedAt, string? completedAt)
        => UpdateStatusAsync(id, status, updatedAt, completedAt);

    public Task<int> CancelAllUnfinishedAsync(string completedAt)
    {
        var unfinished = _store.Values.Where(d => d.Status is "pending" or "running").ToList();
        foreach (var delegation in unfinished)
        {
            delegation.Status = "cancelled";
            delegation.EndedReason = "lost";
            delegation.UpdatedAt = completedAt;
            delegation.CompletedAt = completedAt;
        }
        return Task.FromResult(unfinished.Count);
    }

    public Task UpdateChildSessionIdAsync(string id, string? childSessionId, string updatedAt)
    {
        UpdateChildSessionIdCalls.Add((id, childSessionId, updatedAt));
        if (_store.TryGetValue(id, out var delegation))
        {
            delegation.ChildSessionId = childSessionId;
            delegation.UpdatedAt = updatedAt;
        }
        return Task.CompletedTask;
    }

    public Task UpdateChildSessionIdAsync(IDbConnection connection, IDbTransaction? transaction, string id, string? childSessionId, string updatedAt)
        => UpdateChildSessionIdAsync(id, childSessionId, updatedAt);

    public Task DeleteByParentSessionIdAsync(string parentSessionId)
    {
        DeleteByParentSessionIdCalls.Add(parentSessionId);
        var ids = _store.Values.Where(d => d.ParentSessionId == parentSessionId).Select(d => d.Id).ToList();
        foreach (var id in ids)
            _store.Remove(id);
        return Task.CompletedTask;
    }

    public Task DeleteByParentSessionIdAsync(IDbConnection connection, IDbTransaction? transaction, string parentSessionId)
        => DeleteByParentSessionIdAsync(parentSessionId);
}
