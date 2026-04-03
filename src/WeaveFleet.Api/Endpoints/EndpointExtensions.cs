namespace WeaveFleet.Api.Endpoints;

/// <summary>
/// Extension methods for registering all stub API endpoints.
/// </summary>
public static class EndpointExtensions
{
    public static WebApplication MapFleetEndpoints(this WebApplication app)
    {
        app.MapSessionEndpoints();
        app.MapFleetSummaryEndpoints();
        app.MapConfigEndpoints();
        app.MapDirectoryEndpoints();
        app.MapHarnessEndpoints();
        app.MapWorkspaceRootEndpoints();
        app.MapWebSocketEndpoints();

        return app;
    }
}
