namespace WeaveFleet.Api.Endpoints;

public static class FleetEndpoints
{
    public static WebApplication MapFleetSummaryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("Fleet");

        group.MapGet("/fleet/summary", () => Results.Ok(new
        {
            activeSessions = 0,
            idleSessions = 0,
            totalTokens = 0,
            totalCost = 0.0,
            queuedTasks = 0
        }))
        .WithName("GetFleetSummary");

        group.MapGet("/version", () => Results.Ok(new
        {
            version = "0.1.0-dev",
            commit = "bootstrap"
        }))
        .WithName("GetVersion");

        group.MapGet("/profile", () => Results.Ok(new
        {
            profile = "default"
        }))
        .WithName("GetProfile");

        group.MapGet("/repositories", () => Results.Ok(new
        {
            repositories = Array.Empty<object>(),
            scannedAt = 0
        }))
        .WithName("GetRepositories");

        group.MapGet("/integrations", () => Results.Ok(new
        {
            integrations = Array.Empty<object>()
        }))
        .WithName("GetIntegrations");

        group.MapGet("/skills", () => Results.Ok(Array.Empty<object>()))
        .WithName("GetSkills");

        group.MapGet("/available-tools", () => Results.Ok(Array.Empty<object>()))
        .WithName("GetAvailableTools");

        return app;
    }
}
