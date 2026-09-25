using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

/// <summary>
/// Sessions' queued messages in memory, in order. Unlike the real repository it doesn't check whose session it is;
/// set <see cref="KnownSessions"/> to have it refuse items for any other session.
/// </summary>
public sealed class InMemoryQueuedPromptRepository : IQueuedPromptRepository
{
    private readonly Lock _gate = new();
    private readonly List<(long Position, QueuedPrompt Item)> _items = [];
    private long _next;

    /// <summary>The sessions that take items; null takes any.</summary>
    public HashSet<string>? KnownSessions { get; set; }

    public Task<IReadOnlyList<QueuedPrompt>> ListAsync(string sessionId)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<QueuedPrompt>>(
                _items.Where(i => i.Item.SessionId == sessionId).OrderBy(i => i.Position).Select(i => i.Item).ToList());
        }
    }

    public Task<bool> AddAsync(QueuedPrompt item) => Insert(item, front: false);

    public Task<bool> ReturnToFrontAsync(QueuedPrompt item) => Insert(item, front: true);

    public Task<QueuedPrompt?> TakeAsync(string sessionId, string itemId)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Item.SessionId == sessionId && i.Item.Id == itemId);
            if (index < 0)
                return Task.FromResult<QueuedPrompt?>(null);
            var item = _items[index].Item;
            _items.RemoveAt(index);
            return Task.FromResult<QueuedPrompt?>(item);
        }
    }

    public Task<QueuedPrompt?> TakeFirstAsync(string sessionId)
    {
        lock (_gate)
        {
            var first = _items.Where(i => i.Item.SessionId == sessionId).OrderBy(i => i.Position).FirstOrDefault();
            if (first.Item is null)
                return Task.FromResult<QueuedPrompt?>(null);
            _items.Remove(first);
            return Task.FromResult<QueuedPrompt?>(first.Item);
        }
    }

    private Task<bool> Insert(QueuedPrompt item, bool front)
    {
        lock (_gate)
        {
            if (KnownSessions is { } known && !known.Contains(item.SessionId))
                return Task.FromResult(false);
            var position = front
                ? _items.Where(i => i.Item.SessionId == item.SessionId).Select(i => i.Position).DefaultIfEmpty(1).Min() - 1
                : ++_next;
            _items.Add((position, item));
            return Task.FromResult(true);
        }
    }
}
