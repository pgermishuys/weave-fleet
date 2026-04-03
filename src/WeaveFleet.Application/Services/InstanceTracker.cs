using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Services;

/// <summary>
/// In-memory registry of live <see cref="IHarnessInstance"/> objects, keyed by instance id.
/// Registered as a singleton so orchestrator and endpoints share the same map.
/// </summary>
public sealed class InstanceTracker
{
    private readonly ConcurrentDictionary<string, IHarnessInstance> _instances = new();

    /// <summary>Register a live harness instance.</summary>
    public void Register(string instanceId, IHarnessInstance instance) =>
        _instances[instanceId] = instance;

    /// <summary>Get a live harness instance, or null if not tracked.</summary>
    public IHarnessInstance? Get(string instanceId) =>
        _instances.TryGetValue(instanceId, out var inst) ? inst : null;

    /// <summary>Remove a harness instance from the tracker.</summary>
    public bool Remove(string instanceId) =>
        _instances.TryRemove(instanceId, out _);

    /// <summary>Snapshot of all currently tracked instances.</summary>
    public IReadOnlyDictionary<string, IHarnessInstance> GetAll() =>
        new ReadOnlyDictionary<string, IHarnessInstance>(_instances);
}
