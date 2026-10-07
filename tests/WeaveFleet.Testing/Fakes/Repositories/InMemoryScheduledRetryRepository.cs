using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

/// <summary>Sessions' retries in memory, one per session. Unlike the real repository it doesn't check whose session it is.</summary>
public sealed class InMemoryScheduledRetryRepository : IScheduledRetryRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ScheduledRetry> _retries = new(StringComparer.Ordinal);

    public IReadOnlyList<ScheduledRetry> All
    {
        get
        {
            lock (_gate)
                return _retries.Values.ToList();
        }
    }

    public Task<ScheduledRetry?> GetAsync(string sessionId)
    {
        lock (_gate)
            return Task.FromResult(_retries.GetValueOrDefault(sessionId));
    }

    public Task<IReadOnlyList<ScheduledRetry>> ListForUserAsync() => ListWaitingAsync();

    public Task<IReadOnlyList<ScheduledRetry>> ListWaitingAsync()
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<ScheduledRetry>>(_retries.Values.Where(r => r.State == ScheduledRetryStates.Waiting).ToList());
    }

    public Task<bool> SaveAsync(ScheduledRetry retry)
    {
        lock (_gate)
            _retries[retry.SessionId] = retry;
        return Task.FromResult(true);
    }

    public Task<ScheduledRetry?> TakeWaitingAsync(string sessionId)
    {
        lock (_gate)
        {
            if (!_retries.TryGetValue(sessionId, out var retry) || retry.State != ScheduledRetryStates.Waiting)
                return Task.FromResult<ScheduledRetry?>(null);
            var sent = retry with { State = ScheduledRetryStates.Sent };
            _retries[sessionId] = sent;
            return Task.FromResult<ScheduledRetry?>(sent);
        }
    }

    public Task<ScheduledRetry?> RemoveAsync(string sessionId)
    {
        lock (_gate)
            return Task.FromResult(_retries.Remove(sessionId, out var removed) ? removed : null);
    }
}
