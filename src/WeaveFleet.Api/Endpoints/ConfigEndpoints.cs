using System.Text.Json.Nodes;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder app)
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

    public static IEndpointRouteBuilder MapClientConfigEndpoints(this IEndpointRouteBuilder app, FleetOptions fleetOptions)
    {
        var group = app.MapGroup("/api/config").WithTags("Config");

        group.MapGet("/client", (
            IHarnessRegistry harnessRegistry) =>
        {
            var availableHarnesses = harnessRegistry.GetAll()
                .Select(harness => harness.Type)
                .ToArray();

            return Results.Ok(new ClientConfigResponse(
                fleetOptions.Cloud.Enabled,
                fleetOptions.Auth.Enabled,
                availableHarnesses));
        })
        .Produces<ClientConfigResponse>(StatusCodes.Status200OK)
        .WithName("GetClientConfig");

        return app;
    }
}

internal sealed record ClientConfigResponse(
    bool CloudMode,
    bool AuthEnabled,
    IReadOnlyList<string> AvailableHarnesses);
#pragma warning restore IL2026
