using WeaveFleet.Api.Auth;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Updates;
using WeaveFleet.Infrastructure.Updates;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class UpdateEndpoints
{
    public static IEndpointRouteBuilder MapUpdateEndpoints(this IEndpointRouteBuilder app, FleetOptions fleetOptions)
    {
        var group = app.MapGroup("/api/update").WithTags("Update");
        group.AddEndpointFilter<AgentRequests.WritesAreTheUsers>();

        // GET /api/update/status — current update state
        group.MapGet("/status", (UpdateStateHolder stateHolder, UpdateRestartService restart) =>
        {
            var state = stateHolder.State;
            var capability = restart.Capability();
            return Results.Ok(new UpdateStatusResponse(
                FleetInstrumentation.ServiceVersion.Split('+')[0],
                state.Status.ToString().ToLowerInvariant(),
                state.LatestVersion,
                state.CheckedAt?.ToString("O"),
                state.Error,
                state.DownloadBytesReceived,
                state.DownloadBytesTotal,
                capability.CanRestart,
                capability.Reason,
                restart.RolledBack()));
        })
        .WithName("GetUpdateStatus");

        // GET /api/update/releases — the recent releases' notes, for What's new
        group.MapGet("/releases", async (ReleaseNotesStore releaseNotes, CancellationToken ct) =>
        {
            var notes = await releaseNotes.GetAsync(ct);
            return Results.Ok(new ReleaseNotesResponse(
                notes.Releases.Select(r => new ReleaseNoteResponse(r.Version, r.PublishedAt, r.Body, r.Url)).ToList(),
                notes.FetchedAt?.ToString("O"),
                notes.Error));
        })
        .WithName("GetReleaseNotes");

        // POST /api/update/check — trigger an update check manually
        group.MapPost("/check", async (
            UpdateCheckService checkService,
            CancellationToken ct) =>
        {
            await checkService.CheckForUpdateAsync(ct);
            return Results.Accepted();
        })
        .WithName("TriggerUpdateCheck");

        // POST /api/update/download — trigger download when state is Available
        group.MapPost("/download", (
            UpdateStateHolder stateHolder,
            UpdateDownloadService downloadService,
            CancellationToken ct) =>
        {
            var state = stateHolder.State;
            if (state.Status != UpdateStatus.Available)
                return Results.BadRequest(new ApiErrorResponse("No update is available to download."));

            // Fire-and-forget: download runs in background, client polls /status for progress.
            _ = downloadService.DownloadUpdateAsync(state, ct);
            return Results.Accepted();
        })
        .WithName("TriggerUpdateDownload");

        // Restarting Fleet and starting a rollout are the owner's alone: an agent (the group filter above) or a paired
        // phone must never be able to take Fleet down. Only local mode has an owner token; a hosted Fleet updates itself.
        if (fleetOptions.Auth.Enabled || !fleetOptions.Auth.TokenAuthEnabled)
            return app;

        // POST /api/update/apply — restart into the staged update, now. 409 when nothing is staged, a turn is running,
        // or this Fleet has no way back up; the launcher applies the update and starts the new version.
        group.MapPost("/apply", async (UpdateRestartService restart) =>
        {
            var result = await restart.BeginAsync();
            return result.Outcome == RestartOutcome.Accepted
                ? Results.Json(new RestartingResponse(result.To), ApiJsonContext.Default.RestartingResponse, statusCode: StatusCodes.Status202Accepted)
                : Results.Conflict(new ApiErrorResponse(result.Message ?? "Fleet can't restart now."));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .WithName("ApplyUpdate");

        // POST /api/update/rollout — restart the other machines into their staged update, one at a time.
        group.MapPost("/rollout", async (RolloutRequest? request, UpdateRolloutService rollout) =>
        {
            var run = await rollout.StartAsync(request?.MachineIds);
            return run is null
                ? Results.Conflict(new ApiErrorResponse("A rollout is already running."))
                : Results.Json(run, ApiJsonContext.Default.RolloutRun, statusCode: StatusCodes.Status202Accepted);
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .WithName("StartUpdateRollout");

        // GET /api/update/rollout — the running rollout, or the last one; 204 before the first.
        group.MapGet("/rollout", (UpdateRolloutService rollout) =>
            rollout.Current is { } run ? Results.Json(run, ApiJsonContext.Default.RolloutRun) : Results.NoContent())
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .WithName("GetUpdateRollout");

        // DELETE /api/update/rollout — stop after the machine in hand.
        group.MapDelete("/rollout", (UpdateRolloutService rollout) =>
        {
            rollout.Cancel();
            return Results.Accepted();
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .WithName("CancelUpdateRollout");

        return app;
    }
}

#pragma warning restore IL2026
