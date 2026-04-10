using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Plugins;

namespace WeaveFleet.Api.Endpoints;

/// <summary>
/// Extension methods for registering all stub API endpoints.
/// </summary>
public static class EndpointExtensions
{
    public static WebApplication MapFleetEndpoints(this WebApplication app)
    {
        app.MapPluginEndpoints();
        app.MapSessionEndpoints();
        app.MapProjectEndpoints();
        app.MapFleetSummaryEndpoints();
        app.MapSessionSourceEndpoints();
        app.MapConfigEndpoints();
        app.MapDirectoryEndpoints();
        app.MapOpenDirectoryEndpoints();
        app.MapSkillEndpoints();
        app.MapInstanceEndpoints();
        app.MapHarnessEndpoints();
        app.MapWorkspaceRootEndpoints();
        app.MapWorkspaceEndpoints();
        app.MapSessionEventEndpoints();
        app.MapWebSocketEndpoints();
        app.MapBackendPluginEndpoints();
        app.MapAnalyticsEndpoints();

        return app;
    }

    public static WebApplication MapBackendPluginEndpoints(this WebApplication app)
    {
        foreach (var plugin in app.Services.GetServices<IBackendPlugin>())
        {
            plugin.MapEndpoints(app);
        }

        return app;
    }
}
