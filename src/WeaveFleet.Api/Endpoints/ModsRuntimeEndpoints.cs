using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// The runtime mods run on (Bun): what it is, and installing Fleet's own. Not gated by the Mods switch, since Settings
/// needs them while it is off; installing is the switch-on action, so it turns the switch on for the caller, as
/// <c>PUT /api/preferences/Mods</c> would. They live under <c>/api/features</c> rather than <c>/api/mods</c> because
/// <c>runtime</c> is a valid mod name.
/// </summary>
public static class ModsRuntimeEndpoints
{
    internal const string NothingRunningMessage = "No install is running.";

    public static IEndpointRouteBuilder MapModsRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        // Configured statement by statement, as ModEndpoints is: no fluent chain ends in a filter lambda.
        var runtime = app.MapGroup("/api/features/mods/runtime").WithTags("Mods");

        runtime.MapGet("/", async (IModsRuntime service, CancellationToken ct)
                => Results.Ok(await service.GetViewAsync(ct)))
            .WithName("GetModsRuntime")
            .Produces<ModsRuntimeView>();

        // POST /api/features/mods/runtime/install — turns Mods on for the caller, then installs Fleet's Bun in the background.
        // 200 when it is already installed, 202 when an install started or is already running.
        runtime.MapPost("/install", InstallAsync)
            .WithName("InstallModsRuntime")
            .Produces<ModsRuntimeView>(StatusCodes.Status200OK)
            .Produces<ModsRuntimeView>(StatusCodes.Status202Accepted);

        // POST /api/features/mods/runtime/use — runs mods on a Bun the view listed under "found" and turns Mods on for the
        // caller. Nothing is downloaded. 400 for a path that isn't one of them, 404 in cloud mode, 409 when the configuration sets a Bun.
        runtime.MapPost("/use", UseAsync)
            .WithName("UseModsRuntime")
            .Produces<ModsRuntimeView>();

        runtime.MapPost("/cancel", CancelAsync)
            .WithName("CancelModsRuntimeInstall")
            .Produces<ModsRuntimeView>();

        return app;
    }

    private static async Task<IResult> InstallAsync(
        IModsRuntime service, FleetOptions options, IUserContext user, HttpContext http, CancellationToken ct)
    {
        if (http.RefuseAgent("the Mods switch") is { } refused)
            return refused;

        if (!string.IsNullOrWhiteSpace(options.Harness.BunPath))
            return Results.Conflict(new ApiErrorResponse($"Fleet uses the Bun at {options.Harness.BunPath}, set in its configuration."));

        await TurnModsOnAsync(http, ct);
        var started = await service.StartInstallAsync(user.UserId, ct);
        var view = await service.GetViewAsync(ct);
        return started ? Results.Accepted(uri: (string?)null, view) : Results.Ok(view);
    }

    private static async Task<IResult> UseAsync(
        UseModsRuntimeRequest request, IModsRuntime service, FleetOptions options, IUserContext user, HttpContext http, CancellationToken ct)
    {
        if (http.RefuseAgent("the Mods switch") is { } refused)
            return refused;

        // Cloud mode runs managed machines: there is no user computer whose Bun could be offered.
        if (options.Cloud.Enabled)
            return Results.NotFound(new ApiErrorResponse("Mods can't use your own Bun in cloud mode."));

        if (!string.IsNullOrWhiteSpace(options.Harness.BunPath))
            return Results.Conflict(new ApiErrorResponse($"Fleet uses the Bun at {options.Harness.BunPath}, set in its configuration."));

        if (string.IsNullOrWhiteSpace(request.Path))
            return Results.BadRequest(new ApiErrorResponse("\"path\" is required: the path of a Bun Fleet found."));

        var used = await service.UseAsync(request.Path, ct);
        if (used.IsFailure)
            return Results.BadRequest(new ApiErrorResponse(used.Error.Description));

        await TurnModsOnAsync(http, ct);
        // The host follows the switch. In the background, as PUT /api/preferences/Mods does.
        var host = http.RequestServices.GetRequiredService<IModHost>();
        _ = Task.Run(() => host.EnsureAsync(user.UserId, CancellationToken.None), CancellationToken.None);
        return Results.Ok(await service.GetViewAsync(ct));
    }

    private static async Task<IResult> CancelAsync(IModsRuntime service, CancellationToken ct)
    {
        if (!await service.CancelAsync(ct))
            return Results.Conflict(new ApiErrorResponse(NothingRunningMessage));

        return Results.Ok(await service.GetViewAsync(ct));
    }

    /// <summary>What turning the Mods switch on does (see <c>PUT /api/preferences/Mods</c>): stores it, and ends "Start without mods".</summary>
    private static async Task TurnModsOnAsync(HttpContext http, CancellationToken ct)
    {
        var services = http.RequestServices;
        if (await services.GetRequiredService<ModsFeature>().IsSwitchedOnAsync())
            return;

        await services.GetRequiredService<IUserPreferenceRepository>().SetAsync(ModsFeature.PreferenceKey, "true");
        await services.GetRequiredService<ModService>().SwitchedOnAsync(ct);
    }
}

#pragma warning restore IL2026

/// <summary>The body of <c>POST /api/features/mods/runtime/use</c>.</summary>
/// <param name="Path">One of the paths the view lists under <c>found</c>.</param>
internal sealed record UseModsRuntimeRequest(string? Path);
