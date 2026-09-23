using System.Text.Json.Nodes;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Config");

        // GET /api/config — Fleet's runtime settings.
        group.MapGet("/config", (FleetOptions fleetOptions) =>
        {
            var config = new JsonObject
            {
                ["authEnabled"] = fleetOptions.Auth.Enabled,
                ["tokenAuthEnabled"] = fleetOptions.Auth.TokenAuthEnabled,
                ["pooledOpenCodeHarness"] = fleetOptions.Harness.PooledOpenCodeHarness,
            };
            return Results.Ok(config);
        })
        .WithName("GetConfig");

        // PUT /api/config — changes Fleet's runtime settings. The Weave config lives at /api/weave.
        group.MapPut("/config", (JsonObject? body, FleetOptions fleetOptions) =>
            ApplyRuntimeSettings(body ?? [], fleetOptions) ?? Results.NoContent())
        .WithName("UpdateConfig");

        return app;
    }

    private static IResult? ApplyRuntimeSettings(JsonObject config, FleetOptions fleetOptions)
    {
        if (!config.TryGetPropertyValue("pooledOpenCodeHarness", out var pooledOpenCodeHarnessNode)
            || pooledOpenCodeHarnessNode is null)
        {
            return null;
        }

        if (pooledOpenCodeHarnessNode is not JsonValue pooledOpenCodeHarnessValue
            || !pooledOpenCodeHarnessValue.TryGetValue<bool>(out var pooledOpenCodeHarness))
        {
            return Results.BadRequest(new ApiErrorResponse(
                "pooledOpenCodeHarness must be a boolean value."));
        }

        fleetOptions.Harness.PooledOpenCodeHarness = pooledOpenCodeHarness;
        return null;
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
                fleetOptions.Auth.TokenAuthEnabled,
                availableHarnesses,
                fleetOptions.TerminalEnabled));
        })
        .AllowAnonymous()
        .Produces<ClientConfigResponse>(StatusCodes.Status200OK)
        .WithName("GetClientConfig");

        return app;
    }
}

internal sealed record ClientConfigResponse(
    bool CloudMode,
    bool AuthEnabled,
    bool TokenAuthEnabled,
    IReadOnlyList<string> AvailableHarnesses,
    bool TerminalEnabled);

#pragma warning restore IL2026
