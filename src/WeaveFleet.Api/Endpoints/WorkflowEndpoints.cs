using System.Text.Json.Serialization;
using WeaveFleet.Application.Workflows;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Workflows: the library, starting a run, and answering one that waits on you. With workflows turned off every
/// route answers 404, as if it didn't exist.
/// </summary>
public static class WorkflowEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workflows")
            .WithTags("Workflows")
            .AddEndpointFilter(async (context, next) =>
                await context.HttpContext.RequestServices.GetRequiredService<WorkflowsFeature>().IsEnabledAsync()
                    ? await next(context)
                    : Results.NotFound(new ApiErrorResponse(FleetWorkflows.TurnedOffMessage)));

        // GET /api/workflows?directory=/path/to/repo — the built-ins, then the repo's .weave/workflows/*.yaml.
        group.MapGet("/", async (string? directory, WorkflowService workflows, CancellationToken ct)
                => Results.Ok(await workflows.ListAsync(directory, ct)))
            .WithName("ListWorkflows")
            .Produces<WorkflowLibraryDto>();

        group.MapGet("/runs", async (string? workflowId, int? limit, WorkflowService workflows)
                => Results.Ok(await workflows.ListRunsAsync(workflowId, limit ?? 50)))
            .WithName("ListWorkflowRuns")
            .Produces<IReadOnlyList<WorkflowRunDto>>();

        group.MapGet("/runs/{id}", async (string id, WorkflowService workflows)
                => (await workflows.GetRunAsync(id)).ToApiResult())
            .WithName("GetWorkflowRun")
            .Produces<WorkflowRunDto>();

        group.MapPost("/runs", async (StartWorkflowRunRequest request, WorkflowService workflows, CancellationToken ct)
                => (await workflows.StartAsync(request, ct)).ToApiResult())
            .WithName("StartWorkflowRun")
            .Produces<WorkflowRunDto>();

        // POST /api/workflows/runs/{id}/answer { "choice": "choice:0" | "outcome:pass" | "retry", "note": "…" }
        group.MapPost("/runs/{id}/answer", async (string id, AnswerWorkflowRunRequest request, WorkflowService workflows, CancellationToken ct)
                => (await workflows.AnswerAsync(id, request.Choice, request.Note, ct)).ToApiResult())
            .WithName("AnswerWorkflowRun")
            .Produces<WorkflowRunDto>();

        // Ending a run only stops Fleet advancing it; its sessions stay.
        group.MapPost("/runs/{id}/end", async (string id, WorkflowService workflows, CancellationToken ct)
                => (await workflows.EndAsync(id, ct)).ToApiResult())
            .WithName("EndWorkflowRun")
            .Produces<WorkflowRunDto>();

        return app;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnswerWorkflowRunRequest(string Choice, string? Note = null);

#pragma warning restore IL2026
