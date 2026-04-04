namespace WeaveFleet.Api.Endpoints;

/// <summary>
/// Extension methods for registering all stub API endpoints.
/// </summary>
public static class EndpointExtensions
{
    public static WebApplication MapFleetEndpoints(this WebApplication app)
    {
        app.MapSessionEndpoints();
        app.MapProjectEndpoints();
        app.MapFleetSummaryEndpoints();
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
        app.MapGitHubAuthEndpoints();
        app.MapGitHubEndpoints();
        app.MapAnalyticsEndpoints();

        return app;
    }
}
