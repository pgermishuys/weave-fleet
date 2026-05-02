using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

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

#pragma warning restore IL2026
