using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Services;

/// <summary>
/// In-memory registry of live <see cref="IHarnessSession"/> objects, keyed by instance id.
/// Registered as a singleton so orchestrator and endpoints share the same map.
/// </summary>
public sealed class InstanceTracker
{
    private readonly ConcurrentDictionary<string, IHarnessSession> _instances = new();

    /// <summary>Fires after a harness instance is registered.</summary>
    public event Action<string, IHarnessSession>? InstanceRegistered;

    /// <summary>Fires after a harness instance is removed.</summary>
    public event Action<string>? InstanceRemoved;

    /// <summary>Register a live harness instance.</summary>
    public void Register(string instanceId, IHarnessSession instance)
    {
        _instances[instanceId] = instance;
        InstanceRegistered?.Invoke(instanceId, instance);
    }

    /// <summary>Get a live harness instance, or null if not tracked.</summary>
    public IHarnessSession? Get(string instanceId) =>
        _instances.TryGetValue(instanceId, out var inst) ? inst : null;

    /// <summary>Remove a harness instance from the tracker.</summary>
    public bool Remove(string instanceId)
    {
        var removed = _instances.TryRemove(instanceId, out _);
        if (removed)
            InstanceRemoved?.Invoke(instanceId);
        return removed;
    }

    /// <summary>Snapshot of all currently tracked instances.</summary>
    public IReadOnlyDictionary<string, IHarnessSession> GetAll() =>
        new ReadOnlyDictionary<string, IHarnessSession>(_instances);
}

