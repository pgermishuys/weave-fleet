using WeaveFleet.Api;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class WorkspaceRootEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceRootEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("WorkspaceRoots");

        // Workspace roots are local allowed-root inputs used for directory browsing and
        // repository discovery. Integration-backed session sources are configured elsewhere.
        group.MapGet("/workspace-roots", async (WorkspaceRootService svc) =>
        {
            var roots = await svc.ListRootsAsync();
            var items = roots.Select(r =>
            {
                var isEnv = r.Id.StartsWith("env:", StringComparison.Ordinal);
                return new WorkspaceRootItem(
                    Id: isEnv ? null : r.Id,
                    Path: r.Path,
                    Source: isEnv ? "env" : "user",
                    Exists: Directory.Exists(r.Path));
            }).ToList();
            return Results.Ok(new WorkspaceRootsResponse(items));
        })
        .WithName("GetWorkspaceRoots");

        group.MapPost("/workspace-roots", async (AddWorkspaceRootRequest req, WorkspaceRootService svc) =>
        {
            var result = await svc.AddRootAsync(req.Path);
            return result.Match(
                root => Results.Ok(new WorkspaceRootAddedResponse(root.Id, root.Path)),
                err => err.ToApiResult());
        })
        .WithName("AddWorkspaceRoot");

        group.MapDelete("/workspace-roots/{id}", async (string id, WorkspaceRootService svc) =>
        {
            var result = await svc.RemoveRootAsync(id);
            return result.ToNoContentResult();
        })
        .WithName("DeleteWorkspaceRoot");

        return app;
    }
}

internal sealed record AddWorkspaceRootRequest(string Path);

file static class WorkspaceRootFleetErrorExtensions
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
