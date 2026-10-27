using WeaveFleet.Api.Auth;
using WeaveFleet.Application.Jobs;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Jobs: the queue, and each machine's seats and labels for it. With jobs turned off every route answers 404, as if it
/// didn't exist.
/// </summary>
public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/jobs")
            .WithTags("Jobs")
            // A job starts a prompted session, perhaps on another machine, so only the user posts one for now.
            .AddEndpointFilter(async (context, next) =>
                !HttpMethods.IsGet(context.HttpContext.Request.Method) && context.HttpContext.IsAgentRequest()
                    ? Results.Json(new ErrorResponse("Jobs are posted by you for now."), ApiJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status403Forbidden)
                    : await next(context))
            .AddEndpointFilter(async (context, next) =>
                await context.HttpContext.RequestServices.GetRequiredService<JobsFeature>().IsEnabledAsync()
                    ? await next(context)
                    : Results.NotFound(new ApiErrorResponse(FleetJobs.TurnedOffMessage)));

        // GET /api/jobs — the jobs, each waiting one with why, and every machine's seats.
        group.MapGet("/", async (JobService jobs) => Results.Ok(await jobs.ListAsync()))
            .WithName("ListJobs")
            .Produces<JobsDto>();

        group.MapPost("/", async (PostJobRequest request, JobService jobs) =>
                (await jobs.PostAsync(request)).ToApiResult())
            .WithName("PostJob")
            .Produces<JobDto>();

        // DELETE /api/jobs/{id} — only a job that still waits; one that started is handled in its session.
        group.MapDelete("/{id}", async (string id, JobService jobs) =>
                await jobs.CancelAsync(id)
                    ? Results.NoContent()
                    : Results.NotFound(new ApiErrorResponse("No waiting job with that id. A job that started is stopped in its session.")))
            .WithName("CancelJob");

        // PUT /api/jobs/machines/{id} — how many jobs the machine may run at once, and its labels. Fields left out stay.
        group.MapPut("/machines/{id}", async (string id, SaveJobMachineRequest request, JobService jobs) =>
                (await jobs.SaveMachineAsync(id, request)).ToApiResult())
            .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
            .WithName("SaveJobMachine")
            .Produces<JobMachineDto>();

        return app;
    }
}
