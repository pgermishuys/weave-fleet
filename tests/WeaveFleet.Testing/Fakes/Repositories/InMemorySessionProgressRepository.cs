using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

/// <summary>
/// In-memory <see cref="ISessionProgressRepository"/>. Stores progress for any session id and doesn't scope
/// by user; tests that need ownership rules use the SQLite repository.
/// </summary>
public sealed class InMemorySessionProgressRepository : ISessionProgressRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionProgress> _progress = new(StringComparer.Ordinal);

    public IReadOnlyList<SessionProgress> All
    {
        get
        {
            lock (_gate)
                return [.. _progress.Values];
        }
    }

    public Task<SessionProgress?> GetAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_progress.GetValueOrDefault(sessionId));
    }

    public Task<IReadOnlyDictionary<string, SessionProgress>> GetManyAsync(IReadOnlyCollection<string> sessionIds, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyDictionary<string, SessionProgress> found = sessionIds
                .Where(_progress.ContainsKey)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(id => id, id => _progress[id], StringComparer.Ordinal);
            return Task.FromResult(found);
        }
    }

    public Task<SessionProgress?> GetForOwnerAsync(string sessionId, string userId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_progress.TryGetValue(sessionId, out var progress) && progress.UserId == userId ? progress : null);
    }

    public Task<bool> UpsertAsync(SessionProgress progress, CancellationToken ct)
    {
        lock (_gate)
            _progress[progress.SessionId] = progress;
        return Task.FromResult(true);
    }
}
