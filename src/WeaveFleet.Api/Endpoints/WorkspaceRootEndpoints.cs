using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

public static class WorkspaceRootEndpoints
{
    public static WebApplication MapWorkspaceRootEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("WorkspaceRoots");

        group.MapGet("/workspace-roots", async (WorkspaceRootService svc) =>
        {
            var roots = await svc.ListRootsAsync();
            var items = roots.Select(r =>
            {
                var isEnv = r.Id.StartsWith("env:", StringComparison.Ordinal);
                return new
                {
                    id = isEnv ? (string?)null : r.Id,
                    path = r.Path,
                    source = isEnv ? "env" : "user",
                    exists = Directory.Exists(r.Path)
                };
            });
            return Results.Ok(new { roots = items });
        })
        .WithName("GetWorkspaceRoots");

        group.MapPost("/workspace-roots", async (AddWorkspaceRootRequest req, WorkspaceRootService svc) =>
        {
            var result = await svc.AddRootAsync(req.Path);
            return result.Match(
                root => Results.Ok(new { id = root.Id, path = root.Path }),
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
            var c when c.EndsWith(".NotFound", StringComparison.Ordinal) => Results.NotFound(new { error = error.Description }),
            "General.Conflict" => Results.Conflict(new { error = error.Description }),
            var c when c.StartsWith("Validation.", StringComparison.Ordinal) => Results.BadRequest(new { error = error.Description }),
            _ => Results.Problem(error.Description)
        };
}
