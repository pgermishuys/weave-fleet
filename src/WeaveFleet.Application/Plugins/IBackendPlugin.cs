using Microsoft.AspNetCore.Routing;

namespace WeaveFleet.Application.Plugins;

public interface IBackendPlugin
{
    FleetPluginDescriptor Descriptor { get; }

    Task<PluginStatus> GetStatusAsync(CancellationToken cancellationToken);

    void MapEndpoints(IEndpointRouteBuilder builder);
}
