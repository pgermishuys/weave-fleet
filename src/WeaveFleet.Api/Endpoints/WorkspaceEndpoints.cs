using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspaces").WithTags("Workspaces");

        group.MapGet("/", async (WorkspaceService svc) =>
        {
            var result = await svc.ListWorkspacesAsync();
            return result.ToApiResult();
        })
        .WithName("ListWorkspaces");

        group.MapGet("/{id}", async (string id, WorkspaceService svc) =>
        {
            var result = await svc.GetWorkspaceAsync(id);
            return result.ToApiResult();
        })
        .WithName("GetWorkspace");

        group.MapPatch("/{id}", async (string id, RenameWorkspaceRequest req, WorkspaceService svc) =>
        {
            var result = await svc.UpdateDisplayNameAsync(id, req.DisplayName);
            return result.ToNoContentResult();
        })
        .WithName("RenameWorkspace");

        return app;
    }
}

internal sealed record RenameWorkspaceRequest(string DisplayName);

