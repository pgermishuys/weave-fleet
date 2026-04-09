using Microsoft.AspNetCore.Builder;

namespace WeaveFleet.Application.Plugins;

public interface IBackendPlugin
{
    FleetPluginDescriptor Descriptor { get; }

    Task<PluginStatus> GetStatusAsync(CancellationToken cancellationToken);

    void MapEndpoints(WebApplication app);
}
