using System.Text.Json.Serialization;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Minimal, frozen V1 session API at <c>/api/sessions-v1</c>.
/// Intentionally a separate surface from <see cref="SessionEndpoints"/> so V1 can be frozen
/// independently while V2 continues to evolve.
/// </summary>
public static class SessionV1Endpoints
{
    public static IEndpointRouteBuilder MapSessionV1Endpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions-v1").WithTags("Sessions V1");

        // GET /api/sessions-v1?limit=&offset=&status=&retentionStatus=
        group.MapGet("/", async (
            SessionService sessionService,
            ISessionRepository sessionRepository,
            IProjectRepository projectRepository,
            SessionActivityTracker activityTracker,
            int limit = 100,
            int offset = 0,
            string? status = null,
            string? retentionStatus = null) =>
        {
            IReadOnlyList<string>? statuses = status is not null ? [status] : null;

            var result = await sessionService.ListSessionsAsync(limit, offset, statuses, null, retentionStatus, "v1");
            return await result.Match<Task<IResult>>(async sessions =>
                {
                    var projectNamesById = (await projectRepository.ListAsync())
                        .ToDictionary(project => project.Id, project => project.Name, StringComparer.Ordinal);

                    var parentIdsWithBusyChildren = (await sessionRepository.GetIdsWithActiveChildrenAsync())
                        .ToHashSet(StringComparer.Ordinal);

                    return Results.Ok(sessions.Select(session =>
                        SessionEndpoints.ToListResponseNoOrigin(session, parentIdsWithBusyChildren, projectNamesById, activityTracker)).ToList());
                },
                error => Task.FromResult(Results.Problem(error.Description) as IResult));
        })
        .Produces<List<SessionListResponse>>(200)
        .WithName("GetSessionsV1");

        // GET /api/sessions-v1/{id}
        group.MapGet("/{id}", async (
            string id,
            SessionService sessionService,
            IWorkspaceRepository workspaceRepository) =>
        {
            var result = await sessionService.GetSessionAsync(id);
            return await result.Match<Task<IResult>>(
                async session =>
                {
                    var workspace = await workspaceRepository.GetByIdAsync(session.WorkspaceId);
                    return Results.Ok(new GetSessionResponse(
                        Id: session.Id,
                        InstanceId: session.InstanceId,
                        WorkspaceId: session.WorkspaceId,
                        WorkspaceDirectory: workspace?.Directory ?? session.Directory,
                        WorkspaceDisplayName: workspace?.DisplayName,
                        SourceDirectory: workspace?.SourceDirectory,
                        IsolationStrategy: workspace?.IsolationStrategy ?? "existing",
                        Branch: workspace?.Branch,
                        Title: session.Title,
                        CreatedAt: session.CreatedAt,
                        StoppedAt: session.StoppedAt,
                        ActivityStatus: session.ActivityStatus,
                        LifecycleStatus: session.LifecycleStatus,
                        RetentionStatus: session.RetentionStatus,
                        ArchivedAt: session.ArchivedAt,
                        TotalTokens: session.TotalTokens > 0 ? session.TotalTokens : null,
                        TotalCost: session.TotalCost > 0 ? session.TotalCost : null,
                        HarnessType: session.HarnessType,
                        ProjectId: session.ProjectId,
                        Origin: null));
                },
                error => Task.FromResult(error.ToSessionV1ApiResult()));
        })
        .WithName("GetSessionV1");

        // POST /api/sessions-v1 — create V1 session
        group.MapPost("/", async (CreateSessionV1ApiRequest req, SessionOrchestrator orchestrator) =>
        {
            var result = await orchestrator.CreateSessionAsync(new CreateSessionRequest
            {
                Directory = req.Directory,
                Title = req.Title,
                IsolationStrategy = req.IsolationStrategy,
                Branch = req.Branch,
                HarnessType = req.HarnessType,
                InitialPrompt = req.InitialPrompt,
                ViewMode = "v1"
            });
            return result.Match(
                r => Results.Ok(new CreateSessionApiResponse(r.InstanceId, r.WorkspaceId, r.Session)),
                err => err.ToSessionV1ApiResult());
        })
        .WithName("CreateSessionV1");

        // POST /api/sessions-v1/{id}/abort
        group.MapPost("/{id}/abort", async (string id, SessionOrchestrator orchestrator) =>
        {
            var result = await orchestrator.AbortSessionAsync(id);
            return result.Match(_ => Results.Ok(), err => err.ToSessionV1ApiResult());
        })
        .WithName("AbortSessionV1");

        // POST /api/sessions-v1/{id}/resume
        group.MapPost("/{id}/resume", async (string id, SessionOrchestrator orchestrator) =>
        {
            var result = await orchestrator.ResumeSessionAsync(id);
            return result.Match(
                session => Results.Ok(new ResumeSessionApiResponse(session.InstanceId, session)),
                err => err.ToSessionV1ApiResult());
        })
        .WithName("ResumeSessionV1");

        // POST /api/sessions-v1/{id}/stop
        group.MapPost("/{id}/stop", async (string id, SessionService sessionService) =>
        {
            var result = await sessionService.StopSessionAsync(id);
            return result.ToNoContentResult();
        })
        .WithName("StopSessionV1");

        // DELETE /api/sessions-v1/{id}
        group.MapDelete("/{id}", async (string id, SessionService sessionService) =>
        {
            var result = await sessionService.DeleteSessionAsync(id);
            return result.Match(
                _ => Results.NoContent(),
                error => error.Code switch
                {
                    var c when c.EndsWith(".NotFound", StringComparison.Ordinal) => Results.NotFound(new ErrorResponse(error.Description)),
                    _ => Results.Problem(error.Description)
                });
        })
        .WithName("DeleteSessionV1");

        // PATCH /api/sessions-v1/{id} — rename
        group.MapPatch("/{id}", async (string id, UpdateSessionTitleRequest req, SessionService sessionService) =>
        {
            var result = await sessionService.UpdateSessionTitleAsync(id, req.Title);
            return result.ToNoContentResult();
        })
        .WithName("UpdateSessionTitleV1");

        // PATCH /api/sessions-v1/{id}/retention
        group.MapPatch("/{id}/retention", async (string id, UpdateSessionRetentionRequest req, SessionService sessionService) =>
        {
            var result = await sessionService.UpdateRetentionAsync(id, req.RetentionStatus);
            return result.ToNoContentResult();
        })
        .WithName("UpdateSessionRetentionV1");

        return app;
    }
}

// ── V1 Request record types ──────────────────────────────────────────────────

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
internal sealed record CreateSessionV1ApiRequest(
    string? Directory,
    string? Title,
    string? IsolationStrategy,
    string? Branch,
    string? HarnessType,
    string? InitialPrompt);

// ── V1 FleetError → IResult helper ──────────────────────────────────────────

file static class SessionV1FleetErrorExtensions
{
    public static IResult ToSessionV1ApiResult(this WeaveFleet.Domain.Common.FleetError error) =>
        error.Code switch
        {
            var c when c.EndsWith(".NotFound", StringComparison.Ordinal) => Results.NotFound(new ErrorResponse(error.Description)),
            "General.Conflict" => Results.Conflict(new ErrorResponse(error.Description)),
            var c when c.StartsWith("Validation.", StringComparison.Ordinal) => Results.BadRequest(new ErrorResponse(error.Description)),
            _ => Results.Problem(error.Description)
        };
}

#pragma warning restore IL2026
