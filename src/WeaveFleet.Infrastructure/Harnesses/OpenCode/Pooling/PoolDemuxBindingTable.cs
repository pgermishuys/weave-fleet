using System.Collections.Concurrent;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

/// <summary>
/// Authoritative pooled OpenCode session binding table used to demultiplex
/// shared per-directory SSE streams to live Fleet session consumers.
/// </summary>
internal sealed class PoolDemuxBindingTable : IOpenCodeSseEventBindingResolver
{
    private readonly ConcurrentDictionary<BindingKey, PoolDemuxBinding> _bindings = new();

    public void Bind(
        PooledOpenCodeInstance instance,
        string openCodeSessionId,
        Guid consumerId,
        string fleetSessionId,
        string userId,
        string directory,
        long leaseGeneration)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(openCodeSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (leaseGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseGeneration), leaseGeneration, "Lease generation must be non-negative.");
        }

        var key = new BindingKey(instance, openCodeSessionId);
        var binding = new PoolDemuxBinding(
            instance,
            openCodeSessionId,
            consumerId,
            fleetSessionId,
            userId,
            directory,
            leaseGeneration);

        _bindings.AddOrUpdate(
            key,
            binding,
            (_, existing) => UpdateBinding(existing, binding));
    }

    public bool TryResolveConsumer(
        PooledOpenCodeInstance instance,
        string directory,
        string openCodeSessionId,
        out Guid consumerId,
        out long leaseGeneration)
    {
        if (TryGetBinding(instance, directory, openCodeSessionId, out var binding))
        {
            consumerId = binding.ConsumerId;
            leaseGeneration = binding.LeaseGeneration;
            return true;
        }

        consumerId = Guid.Empty;
        leaseGeneration = 0;
        return false;
    }

    public bool TryGetBinding(
        PooledOpenCodeInstance instance,
        string directory,
        string openCodeSessionId,
        out PoolDemuxBinding binding)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(openCodeSessionId);

        if (!instance.IsAvailable)
        {
            binding = default;
            return false;
        }

        if (!_bindings.TryGetValue(new BindingKey(instance, openCodeSessionId), out binding))
        {
            return false;
        }

        if (!string.Equals(binding.Directory, directory, StringComparison.Ordinal))
        {
            binding = default;
            return false;
        }

        return true;
    }

    public bool TryGetBinding(
        PooledOpenCodeInstance instance,
        string directory,
        string openCodeSessionId,
        long leaseGeneration,
        out PoolDemuxBinding binding)
    {
        if (leaseGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseGeneration), leaseGeneration, "Lease generation must be non-negative.");
        }

        if (!TryGetBinding(instance, directory, openCodeSessionId, out binding))
        {
            return false;
        }

        if (binding.LeaseGeneration == leaseGeneration)
        {
            return true;
        }

        binding = default;
        return false;
    }

    public bool TryVerifyCommandBinding(
        PooledOpenCodeInstance instance,
        string fleetSessionId,
        string userId,
        string openCodeSessionId,
        string directory,
        long leaseGeneration,
        out PoolDemuxBinding binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (!TryGetBinding(instance, directory, openCodeSessionId, leaseGeneration, out binding))
        {
            return false;
        }

        if (string.Equals(binding.FleetSessionId, fleetSessionId, StringComparison.Ordinal)
            && string.Equals(binding.UserId, userId, StringComparison.Ordinal))
        {
            return true;
        }

        binding = default;
        return false;
    }

    public bool Remove(PooledOpenCodeInstance instance, string openCodeSessionId, long leaseGeneration)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(openCodeSessionId);
        if (leaseGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseGeneration), leaseGeneration, "Lease generation must be non-negative.");
        }

        var key = new BindingKey(instance, openCodeSessionId);
        if (!_bindings.TryGetValue(key, out var binding) || binding.LeaseGeneration != leaseGeneration)
        {
            return false;
        }

        return _bindings.TryRemove(new KeyValuePair<BindingKey, PoolDemuxBinding>(key, binding));
    }

    public bool Remove(PooledOpenCodeInstance instance, string openCodeSessionId)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(openCodeSessionId);

        return _bindings.TryRemove(new BindingKey(instance, openCodeSessionId), out _);
    }

    public int RemoveForLease(
        PooledOpenCodeInstance instance,
        Guid consumerId,
        string fleetSessionId,
        string directory,
        long leaseGeneration)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (leaseGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseGeneration), leaseGeneration, "Lease generation must be non-negative.");
        }

        var removed = 0;
        foreach (var pair in _bindings.ToArray())
        {
            var binding = pair.Value;
            if (!ReferenceEquals(binding.Instance, instance)
                || binding.ConsumerId != consumerId
                || binding.LeaseGeneration != leaseGeneration
                || !string.Equals(binding.FleetSessionId, fleetSessionId, StringComparison.Ordinal)
                || !string.Equals(binding.Directory, directory, StringComparison.Ordinal))
            {
                continue;
            }

            if (_bindings.TryRemove(pair))
            {
                removed++;
            }
        }

        return removed;
    }

    public void MoveBindings(
        PooledOpenCodeInstance sourceInstance,
        PooledOpenCodeInstance targetInstance,
        Guid consumerId,
        string directory,
        long sourceLeaseGeneration,
        long targetLeaseGeneration)
    {
        ArgumentNullException.ThrowIfNull(sourceInstance);
        ArgumentNullException.ThrowIfNull(targetInstance);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (sourceLeaseGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceLeaseGeneration), sourceLeaseGeneration, "Lease generation must be non-negative.");
        }

        if (targetLeaseGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetLeaseGeneration), targetLeaseGeneration, "Lease generation must be non-negative.");
        }

        foreach (var pair in _bindings.ToArray())
        {
            var binding = pair.Value;
            if (!ReferenceEquals(binding.Instance, sourceInstance)
                || binding.ConsumerId != consumerId
                || binding.LeaseGeneration != sourceLeaseGeneration
                || !string.Equals(binding.Directory, directory, StringComparison.Ordinal))
            {
                continue;
            }

            var targetBinding = binding with
            {
                Instance = targetInstance,
                LeaseGeneration = targetLeaseGeneration,
            };
            Bind(
                targetInstance,
                targetBinding.OpenCodeSessionId,
                targetBinding.ConsumerId,
                targetBinding.FleetSessionId,
                targetBinding.UserId,
                targetBinding.Directory,
                targetBinding.LeaseGeneration);
            _bindings.TryRemove(pair);
        }
    }

    private static PoolDemuxBinding UpdateBinding(PoolDemuxBinding existing, PoolDemuxBinding replacement)
    {
        if (replacement.LeaseGeneration < existing.LeaseGeneration)
        {
            return existing;
        }

        if (replacement.LeaseGeneration == existing.LeaseGeneration && !IsSameGenerationUpdate(existing, replacement))
        {
            throw new InvalidOperationException("Cannot replace a pooled OpenCode binding with different metadata in the same lease generation.");
        }

        return replacement;
    }

    private static bool IsSameGenerationUpdate(PoolDemuxBinding existing, PoolDemuxBinding replacement)
    {
        return existing.ConsumerId == replacement.ConsumerId
            && string.Equals(existing.FleetSessionId, replacement.FleetSessionId, StringComparison.Ordinal)
            && string.Equals(existing.UserId, replacement.UserId, StringComparison.Ordinal)
            && string.Equals(existing.Directory, replacement.Directory, StringComparison.Ordinal);
    }

    private readonly record struct BindingKey(PooledOpenCodeInstance Instance, string OpenCodeSessionId);
}

internal readonly record struct PoolDemuxBinding(
    PooledOpenCodeInstance Instance,
    string OpenCodeSessionId,
    Guid ConsumerId,
    string FleetSessionId,
    string UserId,
    string Directory,
    long LeaseGeneration);
