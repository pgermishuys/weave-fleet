using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class UpdateEndpoints
{
    public static IEndpointRouteBuilder MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/update").WithTags("Update");

        // GET /api/update/status — current update state
        group.MapGet("/status", (UpdateStateHolder stateHolder) =>
        {
            var state = stateHolder.State;
            return Results.Ok(new UpdateStatusResponse(
                FleetInstrumentation.ServiceVersion.Split('+')[0],
                state.Status.ToString().ToLowerInvariant(),
                state.LatestVersion,
                state.CheckedAt?.ToString("O"),
                state.Error));
        })
        .WithName("GetUpdateStatus");

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
        group.MapPost("/download", async (
            UpdateStateHolder stateHolder,
            UpdateDownloadService downloadService,
            CancellationToken ct) =>
        {
            var state = stateHolder.State;
            if (state.Status != UpdateStatus.Available)
                return Results.BadRequest(new ApiErrorResponse("No update is available to download."));

            await downloadService.DownloadUpdateAsync(state, ct);
            return Results.Accepted();
        })
        .WithName("TriggerUpdateDownload");

        return app;
    }
}

#pragma warning restore IL2026
