using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Events;
namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// Extension methods for registering the in-process event bus with the DI container.
/// </summary>
public static class InProcessServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-process event bus: event store, channels, publisher, projection host,
    /// and fan-out service. Projections are declared via the <paramref name="configure"/> callback.
    /// </summary>
    public static IServiceCollection AddInProcessEventBus(
        this IServiceCollection services,
        Action<InProcessEventBusBuilder> configure)
    {
        var builder = new InProcessEventBusBuilder(services);
        configure(builder);
        services.AddSingleton(new ProjectionRegistry(builder.Entries));

        // Shared channel holder (projection wakeup + fan-out).
        services.AddSingleton<InProcessChannels>();

        services.AddSingleton<InProcessEventStore>();
        services.AddSingleton<InProcessMetrics>();
        services.AddSingleton<IEventPublisher, InProcessEventPublisher>();

        // BackgroundServices — order matters: projection host must drain startup backlog before
        // the app accepts requests (though in practice both start concurrently).
        services.AddHostedService<InProcessProjectionHost>();
        services.AddHostedService<InProcessFanOutService>();

        return services;
    }
}
