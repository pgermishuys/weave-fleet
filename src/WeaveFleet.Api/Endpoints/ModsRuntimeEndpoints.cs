using System.Text.Json.Serialization;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// The runtime mods run on (Bun): what it is, installing Fleet's own, and pointing Fleet at the user's. Not gated by the
/// Mods switch, since Settings needs them while it is off; the two that start using Mods (install, and saving a Bun
/// path) turn the switch on for the caller, as <c>PUT /api/preferences/Mods</c> would. They live under
/// <c>/api/features</c> rather than <c>/api/mods</c> because <c>runtime</c> is a valid mod name.
/// </summary>
public static class ModsRuntimeEndpoints
{
    internal const string AbsolutePathMessage = "The path must start at the root, like /opt/tools/bun/bin/bun or C:\\Tools\\bun\\bun.exe.";

    internal const string ConfigurationMessage = "Fleet's configuration sets Fleet:Harness:BunPath, so it can't be changed here.";

    internal const string NothingRunningMessage = "No install is running.";

    public static IEndpointRouteBuilder MapModsRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        // Configured statement by statement, as ModEndpoints is: no fluent chain ends in a filter lambda.
        var runtime = app.MapGroup("/api/features/mods/runtime").WithTags("Mods");

        runtime.MapGet("/", async (IModsRuntime service, IUserContext user, CancellationToken ct)
                => Results.Ok(await service.GetViewAsync(user.UserId, ct)))
            .WithName("GetModsRuntime")
            .Produces<ModsRuntimeView>();

        // POST /api/features/mods/runtime/install — turns Mods on for the caller, then installs Fleet's Bun in the background.
        // 200 when it is already installed, 202 when an install started or is already running.
        runtime.MapPost("/install", InstallAsync)
            .WithName("InstallModsRuntime")
            .Produces<ModsRuntimeView>(StatusCodes.Status200OK)
            .Produces<ModsRuntimeView>(StatusCodes.Status202Accepted);

        runtime.MapPost("/cancel", CancelAsync)
            .WithName("CancelModsRuntimeInstall")
            .Produces<ModsRuntimeView>();

        // The Buns on this computer Fleet could offer; looking never makes Fleet use one.
        runtime.MapGet("/found", async (IModsRuntime service, CancellationToken ct)
                => Results.Ok(new ModsRuntimeFound(await service.FindOnMachineAsync(ct))))
            .WithName("FindModsRuntimeOnMachine")
            .Produces<ModsRuntimeFound>();

        // PUT /api/features/mods/runtime/bun-path  { "path": "/opt/tools/bun/bin/bun" | null } — the user's own Bun, or none.
        runtime.MapPut("/bun-path", SetBunPathAsync)
            .WithName("SetModsBunPath")
            .Produces<ModsRuntimeView>();

        return app;
    }

    private static async Task<IResult> InstallAsync(
        IModsRuntime service, IBunPathSetting setting, IUserContext user, HttpContext http, CancellationToken ct)
    {
        if (await setting.GetAsync(user.UserId, ct) is { } own)
            return Results.Conflict(new ApiErrorResponse($"Fleet uses your own Bun at {own}. Clear it to use Fleet's own."));

        await TurnModsOnAsync(http, ct);
        var started = await service.StartInstallAsync(kind: null, user.UserId, ct);
        var view = await service.GetViewAsync(user.UserId, ct);
        return started ? Results.Accepted(uri: (string?)null, view) : Results.Ok(view);
    }

    private static async Task<IResult> CancelAsync(IModsRuntime service, IUserContext user, CancellationToken ct)
    {
        if (!await service.CancelAsync(ct))
            return Results.Conflict(new ApiErrorResponse(NothingRunningMessage));

        return Results.Ok(await service.GetViewAsync(user.UserId, ct));
    }

    private static async Task<IResult> SetBunPathAsync(
        SetModsBunPathRequest request, IModsRuntime service, IBunPathSetting setting, IBunRuntime bun, IUserContext user, HttpContext http, CancellationToken ct)
    {
        if (setting.FromConfiguration is not null)
            return Results.Conflict(new ApiErrorResponse(ConfigurationMessage));

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            await service.SaveBunPathAsync(user.UserId, null, ct);
            return Results.Ok(await service.GetViewAsync(user.UserId, ct));
        }

        var path = request.Path.Trim();
        if (!Path.IsPathFullyQualified(path))
            return Results.BadRequest(new ApiErrorResponse(AbsolutePathMessage));

        var candidate = await bun.CheckAsync(path, ct);
        if (candidate.Status != BunCandidateStatuses.Usable)
            return Results.BadRequest(new ApiErrorResponse(candidate.Message ?? "Fleet couldn't run that Bun."));

        await TurnModsOnAsync(http, ct);
        await service.SaveBunPathAsync(user.UserId, candidate.Path, ct);
        return Results.Ok(await service.GetViewAsync(user.UserId, ct));
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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SetModsBunPathRequest(string? Path);

/// <summary>The Buns found on this computer.</summary>
internal sealed record ModsRuntimeFound(IReadOnlyList<ModsRuntimeCandidate> Candidates);

#pragma warning restore IL2026
