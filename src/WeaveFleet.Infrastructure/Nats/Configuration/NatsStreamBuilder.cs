using Microsoft.Extensions.DependencyInjection;

namespace WeaveFleet.Infrastructure.Nats.Configuration;

/// <summary>
/// Identifies whether a projection's durable consumer is shared cluster-wide (exactly-once
/// writes, correct for persistence-style projections) or per-node (every Fleet node gets its
/// own copy, correct for fan-out-style projections such as WebSocket broadcast).
/// </summary>
public enum ConsumerScope { Cluster, PerNode }

public sealed class NatsStreamBuilder
{
    private readonly IServiceCollection _services;
    internal NatsStreamBuilder(IServiceCollection services) => _services = services;

    internal readonly List<ProjectionRegistryEntry> Entries = new();

    public NatsStreamBuilder AddProjection<TProjection>(ConsumerScope scope = ConsumerScope.Cluster)
        where TProjection : class
    {
        _services.AddScoped<TProjection>();
        Entries.Add(new ProjectionRegistryEntry(typeof(TProjection), scope));
        return this;
    }
}

public sealed record ProjectionRegistryEntry(Type ProjectionType, ConsumerScope Scope);
public sealed record ProjectionRegistry(IReadOnlyList<ProjectionRegistryEntry> Entries);
