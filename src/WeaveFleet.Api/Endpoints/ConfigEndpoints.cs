using System.Text.Json.Nodes;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

public static class ConfigEndpoints
{
    public static WebApplication MapConfigEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("Config");

        // GET /api/config?directory= — returns merged config (user + optional project-level)
        group.MapGet("/config", async (
            string? directory,
            ConfigService configService,
            CancellationToken ct) =>
        {
            var config = await configService.GetMergedConfigAsync(directory, ct);
            return Results.Ok(config);
        })
        .WithName("GetConfig");

        // PUT /api/config — writes user-level config
        group.MapPut("/config", async (
            JsonObject? body,
            ConfigService configService,
            CancellationToken ct) =>
        {
            var config = body ?? [];
            await configService.UpdateUserConfigAsync(config, ct);
            return Results.NoContent();
        })
        .WithName("UpdateConfig");

        // GET /api/config/paths — returns file paths for debugging/display
        group.MapGet("/config/paths", () =>
        {
            var paths = ConfigService.GetConfigPaths();
            return Results.Ok(new
            {
                configDirectory = paths.ConfigDirectory,
                userConfigPath = paths.UserConfigPath
            });
        })
        .WithName("GetConfigPaths");

        return app;
    }
}
