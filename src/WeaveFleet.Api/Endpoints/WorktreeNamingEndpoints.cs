using WeaveFleet.Application.Services;
using WeaveFleet.Application.Services.Worktrees;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class WorktreeNamingEndpoints
{
    public static IEndpointRouteBuilder MapWorktreeNamingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/worktrees/naming").WithTags("Worktrees");

        // GET /api/worktrees/naming?directory= — the templates in force, and where each came from.
        group.MapGet("/", async (
            string? directory,
            WorktreeNamingService naming,
            CancellationToken ct) =>
        {
            var resolved = await naming.GetAsync(directory, ct);
            var user = await naming.GetUserAsync(ct);
            return Results.Ok(Response(resolved, user));
        })
        .Produces<WorktreeNamingResponse>(StatusCodes.Status200OK)
        .WithName("GetWorktreeNaming");

        // PUT /api/worktrees/naming — the user's own layer. A template that can't name a worktree
        // is refused here, so nobody finds out at `git worktree add`.
        group.MapPut("/", async (
            WorktreeNamingRequest body,
            WorktreeNamingService naming,
            CancellationToken ct) =>
        {
            var layer = new WorktreeNamingOverride
            {
                Branch = Trimmed(body.Branch),
                Root = Trimmed(body.Root),
                Folder = Trimmed(body.Folder),
                Initials = Trimmed(body.Initials),
                Capture = body.Capture is { Count: > 0 } capture
                    ? capture.Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
                        .ToDictionary(entry => entry.Key, entry => entry.Value.Trim(), StringComparer.Ordinal)
                    : null,
            };

            var result = await naming.SaveUserAsync(layer, ct);
            if (result.IsFailure)
                return result.Error.ToErrorResult();

            var resolved = await naming.GetAsync(null, ct);
            return Results.Ok(Response(resolved, layer));
        })
        .Produces<WorktreeNamingResponse>(StatusCodes.Status200OK)
        .WithName("UpdateWorktreeNaming");

        // POST /api/worktrees/naming/preview — what a message would be called in a repository.
        // The composer previews locally as you type; this is the authoritative answer.
        group.MapPost("/preview", async (
            WorktreeNamingPreviewRequest body,
            WorktreeNamingService naming,
            WorkspaceRootService workspaceRoots,
            CancellationToken ct) =>
        {
            var pathResult = await workspaceRoots.ResolvePathWithinAllowedRootsAsync(body.Directory);
            if (pathResult.IsFailure)
                return pathResult.Error.ToErrorResult();

            var resolved = await naming.GetAsync(pathResult.Value, ct);
            var context = WorktreeNamingService.BuildContext(pathResult.Value, "00000000");
            var names = WorktreeNameResolver.Resolve(resolved.Effective, context, body.Message, body.Branch);

            return Results.Ok(new WorktreeNamingPreviewResponse(
                names.Branch,
                names.Root,
                names.Folder,
                Path.Combine(names.Root, names.Folder)));
        })
        .Produces<WorktreeNamingPreviewResponse>(StatusCodes.Status200OK)
        .WithName("PreviewWorktreeNaming");

        return app;
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static WorktreeNamingResponse Response(ResolvedWorktreeNaming resolved, WorktreeNamingOverride user) => new(
        new WorktreeNamingTemplates(
            resolved.Effective.Branch,
            resolved.Effective.Root,
            resolved.Effective.Folder,
            resolved.Effective.Capture.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            resolved.Effective.Initials),
        new WorktreeNamingTemplates(
            user.Branch,
            user.Root,
            user.Folder,
            user.Capture?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            user.Initials),
        new WorktreeNamingTemplates(
            WorktreeNaming.DefaultBranch,
            WorktreeNaming.DefaultRoot,
            WorktreeNaming.DefaultFolder,
            null,
            null),
        resolved.Layers.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.ToString().ToLowerInvariant(),
            StringComparer.Ordinal));
}

/// <summary>A layer's templates. Null fields are the ones that layer doesn't set.</summary>
public sealed record WorktreeNamingTemplates(
    string? Branch,
    string? Root,
    string? Folder,
    IReadOnlyDictionary<string, string>? Capture,
    string? Initials);

/// <summary>
/// The templates in force, the user's own layer for Settings to edit, Fleet's defaults, and which
/// layer won each field ("default", "user" or "project").
/// </summary>
public sealed record WorktreeNamingResponse(
    WorktreeNamingTemplates Effective,
    WorktreeNamingTemplates User,
    WorktreeNamingTemplates Defaults,
    IReadOnlyDictionary<string, string> Layers);

public sealed record WorktreeNamingRequest(
    string? Branch,
    string? Root,
    string? Folder,
    IReadOnlyDictionary<string, string>? Capture,
    string? Initials);

public sealed record WorktreeNamingPreviewRequest(string Directory, string? Message, string? Branch);

/// <summary>A branch of null means this message would leave the naming to the server.</summary>
public sealed record WorktreeNamingPreviewResponse(string? Branch, string Root, string Folder, string Path);

#pragma warning restore IL2026
