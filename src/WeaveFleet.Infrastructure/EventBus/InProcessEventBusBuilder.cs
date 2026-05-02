using Microsoft.Extensions.DependencyInjection;

namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// Fluent builder for registering projections with the in-process event bus.
/// </summary>
public sealed class InProcessEventBusBuilder
{
    private readonly IServiceCollection _services;

    internal InProcessEventBusBuilder(IServiceCollection services) => _services = services;

    internal readonly List<ProjectionRegistryEntry> Entries = new();

    /// <summary>
    /// Registers a projection to receive durable events. <paramref name="scope"/> is accepted
    /// for API consistency but has no effect on the in-process path (all dispatches are local,
    /// so Cluster and PerNode are equivalent).
    /// </summary>
    public InProcessEventBusBuilder AddProjection<TProjection>(ConsumerScope scope = ConsumerScope.Cluster)
        where TProjection : class
    {
        _services.AddScoped<TProjection>();
        Entries.Add(new ProjectionRegistryEntry(typeof(TProjection), scope));
        return this;
    }
}
