using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class KeyFileEndpoints
{
    public static IEndpointRouteBuilder MapKeyFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("KeyFiles");

        // GET /api/key-files?directory=... — scan workspace for key project files
        group.MapGet("/key-files", async (string? directory, WorkspaceRootService workspaceRootService, KeyFileScanner scanner, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return Results.BadRequest(new ErrorResponse("Directory does not exist."));

            var normalised = Path.GetFullPath(directory);
            var allowedRoots = await workspaceRootService.GetAllowedRootsAsync();
            if (!OpenDirectoryEndpoints.IsUnderAllowedRoot(normalised, allowedRoots))
                return Results.BadRequest(new ErrorResponse("Path is outside allowed workspace roots."));

            var result = await scanner.ScanAsync(normalised, ct);
            return Results.Ok(new KeyFilesResponse(result.FilesByToolId));
        })
        .WithName("GetKeyFiles");

        return app;
    }
}

internal sealed record KeyFilesResponse(IReadOnlyDictionary<string, IReadOnlyList<string>> FilesByTool);

#pragma warning restore IL2026
