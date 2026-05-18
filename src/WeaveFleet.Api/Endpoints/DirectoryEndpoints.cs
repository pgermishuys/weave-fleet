using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class DirectoryEndpoints
{
    public static IEndpointRouteBuilder MapDirectoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Directories");

        // GET /api/directories?path= — lists subdirectories at the given path
        // Returns workspace roots if no path provided
        group.MapGet("/directories", async (
            string? path,
            bool? unconstrained,
            DirectoryService directoryService,
            CancellationToken ct) =>
        {
            var result = unconstrained == true
                ? await directoryService.ListDirectoryUnconstrainedAsync(path, ct)
                : await directoryService.ListDirectoryAsync(path, ct);
            return Results.Ok(new DirectoryListingResponse(
                Entries: result.Entries.Select(e => new DirectoryEntryResponse(
                    Name: e.Name,
                    Path: e.FullPath,
                    IsGitRepo: e.IsGitRepo,
                    IsRoot: e.IsRoot)).ToList(),
                CurrentPath: result.CurrentPath,
                ParentPath: result.ParentPath,
                Roots: result.Roots));
        })
        .WithName("GetDirectories");

        return app;
    }
}

public sealed record DirectoryListingResponse(
    IReadOnlyList<DirectoryEntryResponse> Entries,
    string? CurrentPath,
    string? ParentPath,
    IReadOnlyList<string> Roots);

public sealed record DirectoryEntryResponse(
    string Name,
    string Path,
    bool IsGitRepo,
    bool IsRoot);
#pragma warning restore IL2026
