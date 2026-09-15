using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemoryAutomationRunRepository : IAutomationRunRepository
{
    private readonly List<AutomationRun> _runs = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<AutomationRun> All
    {
        get
        {
            lock (_gate)
                return [.. _runs];
        }
    }

    public Task InsertAsync(AutomationRun run)
    {
        lock (_gate)
            _runs.Add(Copy(run));
        return Task.CompletedTask;
    }

    public Task CompleteAsync(string id, string status, string? sessionId, string? instanceId, string? reason)
    {
        lock (_gate)
        {
            var run = _runs.Single(r => r.Id == id);
            run.Status = status;
            run.SessionId = sessionId;
            run.InstanceId = instanceId;
            run.Error = reason;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AutomationRun>> ListByAutomationAsync(string automationId, int limit)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<AutomationRun>>(Newest()
                .Where(r => r.AutomationId == automationId)
                .Take(limit)
                .Select(Copy)
                .ToList());
        }
    }

    public Task<IReadOnlyDictionary<string, AutomationRun>> GetLatestPerAutomationAsync()
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyDictionary<string, AutomationRun>>(Newest()
                .GroupBy(r => r.AutomationId)
                .ToDictionary(g => g.Key, g => Copy(g.First())));
        }
    }

    public Task<IReadOnlyDictionary<string, string>> GetLastScheduledForAsync()
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>>(_runs
                .Where(r => r.ScheduledFor is not null)
                .GroupBy(r => r.AutomationId)
                .ToDictionary(g => g.Key, g => g.Max(r => r.ScheduledFor!)!));
        }
    }

    public Task<int> FailStaleStartingAsync(string beforeUtc, string reason)
    {
        lock (_gate)
        {
            var stale = _runs.Where(r => r.Status == AutomationRunStatus.Starting && string.CompareOrdinal(r.StartedAt, beforeUtc) < 0).ToList();
            foreach (var run in stale)
            {
                run.Status = AutomationRunStatus.Failed;
                run.Error = reason;
            }

            return Task.FromResult(stale.Count);
        }
    }

    private IEnumerable<AutomationRun> Newest() =>
        _runs.OrderByDescending(r => r.StartedAt, StringComparer.Ordinal).ThenByDescending(r => r.Id, StringComparer.Ordinal);

    private static AutomationRun Copy(AutomationRun run) => new()
    {
        Id = run.Id,
        AutomationId = run.AutomationId,
        UserId = run.UserId,
        Trigger = run.Trigger,
        ScheduledFor = run.ScheduledFor,
        StartedAt = run.StartedAt,
        Status = run.Status,
        SessionId = run.SessionId,
        InstanceId = run.InstanceId,
        Error = run.Error,
    };
}
