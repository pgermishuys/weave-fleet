using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Routing;

namespace WeaveFleet.Application.Plugins;

public interface IBackendPlugin
{
    FleetPluginDescriptor Descriptor { get; }

    Task<PluginStatus> GetStatusAsync(CancellationToken cancellationToken);

    [RequiresUnreferencedCode("Plugin endpoint delegates are registered at runtime via reflection-based ASP.NET Core route building; all parameter types are concrete and preserved.")]
    [RequiresDynamicCode("Plugin endpoint delegates are registered at runtime via reflection-based ASP.NET Core route building; all parameter types are concrete and preserved.")]
    void MapEndpoints(IEndpointRouteBuilder builder);
}
