namespace WeaveFleet.Api.Endpoints;

public static class WorkspaceRootEndpoints
{
    public static WebApplication MapWorkspaceRootEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("WorkspaceRoots");

        group.MapGet("/workspace-roots", () => Results.Ok(new
        {
            roots = Array.Empty<object>()
        }))
        .WithName("GetWorkspaceRoots");

        group.MapPost("/workspace-roots", () =>
            Results.Json(new { error = "Not implemented" }, statusCode: 501))
        .WithName("AddWorkspaceRoot");

        group.MapDelete("/workspace-roots/{path}", (string path) =>
            Results.Json(new { error = "Not implemented" }, statusCode: 501))
        .WithName("DeleteWorkspaceRoot");

        return app;
    }
}
