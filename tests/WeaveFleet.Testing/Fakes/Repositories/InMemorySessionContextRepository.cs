using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

/// <summary>
/// In-memory <see cref="ISessionContextRepository"/>. Stores a context for any session id; <see cref="GetAsync"/>
/// doesn't scope by user, tests that need ownership rules use the SQLite repository.
/// </summary>
public sealed class InMemorySessionContextRepository : ISessionContextRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SessionContext> _contexts = new(StringComparer.Ordinal);

    public SessionContext? this[string sessionId]
    {
        get
        {
            lock (_gate)
                return _contexts.GetValueOrDefault(sessionId);
        }
    }

    public Task<SessionContext?> GetAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_contexts.GetValueOrDefault(sessionId));
    }

    public Task<SessionContext?> GetForOwnerAsync(string sessionId, string userId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_contexts.TryGetValue(sessionId, out var context) && context.UserId == userId ? context : null);
    }

    public Task<bool> UpsertAsync(SessionContext context, CancellationToken ct)
    {
        lock (_gate)
            _contexts[context.SessionId] = context;
        return Task.FromResult(true);
    }
}
