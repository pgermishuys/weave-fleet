using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Running work across sessions. A session's own work is under <c>/api/sessions/{id}/work</c>; live changes arrive on
/// the <c>sessions</c> topic as <c>work.started</c>, <c>work.updated</c> and <c>work.ended</c>.
/// </summary>
public static class WorkEndpoints
{
    public static IEndpointRouteBuilder MapWorkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/work").WithTags("Work");

        // GET /api/work/running — the user's running work in every session, oldest first: the status bar's counter.
        group.MapGet("/running", async (DelegationService work) => Results.Ok(await work.GetAllRunningWorkAsync()))
            .Produces<IReadOnlyList<RunningWorkItem>>(200)
            .WithName("GetRunningWork");

        return app;
    }
}
#pragma warning restore IL2026
