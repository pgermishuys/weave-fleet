using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

public static class DirectoryEndpoints
{
    public static WebApplication MapDirectoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("Directories");

        // GET /api/directories?path= — lists subdirectories at the given path
        // Returns workspace roots if no path provided
        group.MapGet("/directories", async (
            string? path,
            DirectoryService directoryService,
            CancellationToken ct) =>
        {
            var result = await directoryService.ListDirectoryAsync(path, ct);
            return Results.Ok(new
            {
                entries = result.Entries.Select(e => new
                {
                    name = e.Name,
                    fullPath = e.FullPath,
                    isGitRepo = e.IsGitRepo,
                    isRoot = e.IsRoot
                }),
                currentPath = result.CurrentPath,
                parentPath = result.ParentPath,
                roots = result.Roots
            });
        })
        .WithName("GetDirectories");

        return app;
    }
}
