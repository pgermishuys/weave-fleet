using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Events;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// Extension methods for registering the in-process event bus with the DI container.
/// </summary>
public static class InProcessServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-process event bus: channels, publisher, and fan-out service.
    /// </summary>
    public static IServiceCollection AddInProcessEventBus(this IServiceCollection services)
    {
        // Shared channel holder (fan-out + automation events).
        var channels = new InProcessChannels();
        services.AddSingleton(channels);

        // Expose automation event channel for AutomationEventDispatcherService
        services.AddSingleton(channels.AutomationEvents);

        services.AddSingleton<InProcessMetrics>();
        services.AddSingleton<PipelineLatencyMetrics>();
        services.AddSingleton<IEventPublisher, InProcessEventPublisher>();

        // BackgroundServices
        services.AddHostedService<InProcessFanOutService>();

        return services;
    }
}
