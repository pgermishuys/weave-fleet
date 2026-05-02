using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Infrastructure.Nats.Configuration;

namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// Fluent builder for registering projections with the in-process event bus.
/// Mirrors the API of <see cref="NatsStreamBuilder"/> so callers use an identical pattern
/// regardless of which transport is active.
/// </summary>
public sealed class InProcessEventBusBuilder
{
    private readonly IServiceCollection _services;

    internal InProcessEventBusBuilder(IServiceCollection services) => _services = services;

    internal readonly List<ProjectionRegistryEntry> Entries = new();

    /// <summary>
    /// Registers a projection to receive durable events. <paramref name="scope"/> is accepted
    /// for API parity with the NATS builder but has no effect on the in-process path (all
    /// dispatches are local, so Cluster and PerNode are equivalent).
    /// </summary>
    public InProcessEventBusBuilder AddProjection<TProjection>(ConsumerScope scope = ConsumerScope.Cluster)
        where TProjection : class
    {
        _services.AddScoped<TProjection>();
        Entries.Add(new ProjectionRegistryEntry(typeof(TProjection), scope));
        return this;
    }
}
