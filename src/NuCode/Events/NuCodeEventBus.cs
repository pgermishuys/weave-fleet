using System.Collections.Concurrent;

namespace NuCode.Events;

/// <summary>
/// Thread-safe event bus implementation. Supports typed subscriptions and wildcard (all-event) subscribers.
/// Designed for scoped or singleton use.
/// </summary>
internal sealed class NuCodeEventBus : INuCodeEventBus
{
    private readonly ConcurrentDictionary<string, List<Action<NuCodeEvent>>> _typed = new();
    private readonly List<Action<NuCodeEvent>> _wildcards = [];
    private readonly object _lock = new();

    public void Publish<TProperties>(NuCodeEventDefinition<TProperties> definition, TProperties properties)
    {
        var evt = definition.Create(properties);
        NotifyTyped(definition.Type, evt);
        NotifyWildcards(evt);
    }

    public IDisposable Subscribe<TProperties>(
        NuCodeEventDefinition<TProperties> definition,
        Action<NuCodeEvent<TProperties>> callback)
    {
        // Wrap typed callback as untyped for storage
        void Wrapper(NuCodeEvent e)
        {
            if (e is NuCodeEvent<TProperties> typed)
            {
                callback(typed);
            }
        }

        var subscribers = _typed.GetOrAdd(definition.Type, _ => []);
        lock (_lock)
        {
            subscribers.Add(Wrapper);
        }

        return new Subscription(() =>
        {
            lock (_lock)
            {
                subscribers.Remove(Wrapper);
            }
        });
    }

    public IDisposable SubscribeAll(Action<NuCodeEvent> callback)
    {
        lock (_lock)
        {
            _wildcards.Add(callback);
        }

        return new Subscription(() =>
        {
            lock (_lock)
            {
                _wildcards.Remove(callback);
            }
        });
    }

    private void NotifyTyped(string type, NuCodeEvent evt)
    {
        if (!_typed.TryGetValue(type, out var subscribers))
        {
            return;
        }

        Action<NuCodeEvent>[] snapshot;
        lock (_lock)
        {
            snapshot = [.. subscribers];
        }

        foreach (var subscriber in snapshot)
        {
            subscriber(evt);
        }
    }

    private void NotifyWildcards(NuCodeEvent evt)
    {
        Action<NuCodeEvent>[] snapshot;
        lock (_lock)
        {
            snapshot = [.. _wildcards];
        }

        foreach (var subscriber in snapshot)
        {
            subscriber(evt);
        }
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
            }
        }
    }
}
