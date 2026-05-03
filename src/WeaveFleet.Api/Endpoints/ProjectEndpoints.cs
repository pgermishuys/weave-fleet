using WeaveFleet.Api;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects").WithTags("Projects");

        // GET /api/projects
        group.MapGet("/", async (
            ProjectService projectService,
            IProjectRepository projectRepository) =>
        {
            var result = await projectService.ListProjectsAsync();
            if (result.IsFailure)
                return Results.Problem(result.Error.Description);

            var projects = result.Value;
            var responses = new List<ProjectResponse>(projects.Count);

            foreach (var p in projects)
            {
                var count = await projectRepository.GetSessionCountAsync(p.Id);
                responses.Add(new ProjectResponse(p.Id, p.Name, p.Description, p.Type, p.Position, count, p.CreatedAt, p.UpdatedAt));
            }

            return Results.Ok(responses);
        })
        .Produces<List<ProjectResponse>>(200)
        .WithName("GetProjects");

        // GET /api/projects/{id}
        group.MapGet("/{id}", async (
            string id,
            ProjectService projectService,
            IProjectRepository projectRepository) =>
        {
            var result = await projectService.GetProjectAsync(id);
            if (result.IsFailure)
                return result.ToApiResult();

            var p = result.Value;
            var count = await projectRepository.GetSessionCountAsync(p.Id);
            return Results.Ok(new ProjectResponse(p.Id, p.Name, p.Description, p.Type, p.Position, count, p.CreatedAt, p.UpdatedAt));
        })
        .WithName("GetProject");

        // POST /api/projects
        group.MapPost("/", async (CreateProjectRequest req, ProjectService projectService) =>
        {
            var result = await projectService.CreateProjectAsync(req.Name, req.Description);
            return result.Match(
                p => Results.Created($"/api/projects/{p.Id}",
                    new ProjectResponse(p.Id, p.Name, p.Description, p.Type, p.Position, 0, p.CreatedAt, p.UpdatedAt)),
                error => Results.BadRequest(new ErrorResponse(error.Description)));
        })
        .Produces<ProjectResponse>(201)
        .WithName("CreateProject");

        // PATCH /api/projects/{id}
        group.MapPatch("/{id}", async (string id, UpdateProjectRequest req, ProjectService projectService, IProjectRepository projectRepository) =>
        {
            var result = await projectService.UpdateProjectAsync(id, req.Name, req.Description);
            if (result.IsFailure)
                return result.Error.ToApiResult();

            var p = result.Value;
            var count = await projectRepository.GetSessionCountAsync(p.Id);
            return Results.Ok(new ProjectResponse(p.Id, p.Name, p.Description, p.Type, p.Position, count, p.CreatedAt, p.UpdatedAt));
        })
        .WithName("UpdateProject");

        // DELETE /api/projects/{id}?mode=move_to_scratch|delete_sessions
        group.MapDelete("/{id}", async (string id, string mode, ProjectService projectService) =>
        {
            var result = await projectService.DeleteProjectAsync(id, mode);
            return result.ToNoContentResult();
        })
        .WithName("DeleteProject");

        // PATCH /api/projects/{id}/reorder
        group.MapPatch("/{id}/reorder", async (string id, ReorderProjectRequest req, ProjectService projectService) =>
        {
            var result = await projectService.ReorderProjectAsync(id, req.Position);
            return result.ToNoContentResult();
        })
        .WithName("ReorderProject");

        return app;
    }
}

// Extension helper for FleetError → IResult (used in inline lambdas above)
file static class FleetErrorExtensions
{
    public static IResult ToApiResult(this WeaveFleet.Domain.Common.FleetError error) =>
        error.Code switch
        {
            var c when c.EndsWith(".NotFound", StringComparison.Ordinal) => Results.NotFound(new ErrorResponse(error.Description)),
            "General.Conflict" => Results.Conflict(new ErrorResponse(error.Description)),
            var c when c.StartsWith("Validation.", StringComparison.Ordinal) => Results.BadRequest(new ErrorResponse(error.Description)),
            _ => Results.Problem(error.Description)
        };
}
#pragma warning restore IL2026
