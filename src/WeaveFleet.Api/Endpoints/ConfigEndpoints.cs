namespace WeaveFleet.Api.Endpoints;

public static class ConfigEndpoints
{
    public static WebApplication MapConfigEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("Config");

        group.MapGet("/config", () => Results.Ok(new { }))
        .WithName("GetConfig");

        return app;
    }
}
