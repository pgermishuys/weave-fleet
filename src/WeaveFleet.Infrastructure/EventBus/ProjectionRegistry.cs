namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// Identifies whether a projection's durable consumer is shared cluster-wide (exactly-once
/// writes, correct for persistence-style projections) or per-node (every Fleet node gets its
/// own copy, correct for fan-out-style projections such as WebSocket broadcast).
/// </summary>
public enum ConsumerScope { Cluster, PerNode }

public sealed record ProjectionRegistryEntry(Type ProjectionType, ConsumerScope Scope);
public sealed record ProjectionRegistry(IReadOnlyList<ProjectionRegistryEntry> Entries);
