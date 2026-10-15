using WeaveFleet.Application.Users;
using WeaveFleet.Application.Workspaces;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>Settings → Storage: the worktree checkouts Fleet made, what Clean now would do with each, and doing it.</summary>
public static class StorageEndpoints
{
    public static IEndpointRouteBuilder MapStorageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/storage/worktrees").WithTags("Storage");

        // GET /api/storage/worktrees — every checkout still on disk with its verdict and why; no sizes (they're slow).
        group.MapGet("/", async (WorktreeCleanup cleanup, CancellationToken ct) =>
            Results.Ok(new WorktreeCheckoutsResponse(await cleanup.ListAsync(ct), WorktreeCleanup.ArchivedDays)))
        .Produces<WorktreeCheckoutsResponse>(StatusCodes.Status200OK)
        .WithName("ListWorktreeCheckouts");

        // GET /api/storage/worktrees/{workspaceId}/size — measured once per change of the folder.
        group.MapGet("/{workspaceId}/size", async (string workspaceId, WorktreeCleanup cleanup) =>
            await cleanup.MeasureAsync(workspaceId) is { } size
                ? Results.Ok(size)
                : Results.NotFound(new ErrorResponse("That checkout isn't on disk.")))
        .Produces<FolderSize>(StatusCodes.Status200OK)
        .WithName("GetWorktreeCheckoutSize");

        // POST /api/storage/worktrees/clean — removes the checkouts named that may still go; says what it kept and why.
        group.MapPost("/clean", async (CleanWorktreesRequest body, WorktreeCleanup cleanup, CancellationToken ct) =>
            Results.Ok(await cleanup.CleanAsync(body.WorkspaceIds, ct)))
        .Produces<WorktreeCleanupReport>(StatusCodes.Status200OK)
        .WithName("CleanWorktreeCheckouts");

        // POST /api/storage/worktrees/{workspaceId}/restore — puts a removed checkout back now.
        group.MapPost("/{workspaceId}/restore", async (string workspaceId, WorktreeCleanup cleanup, IUserContext user, CancellationToken ct) =>
            (await cleanup.RestoreAsync(workspaceId, user.UserId, ct)).ToNoContentResult())
        .WithName("RestoreWorktreeCheckout");

        return app;
    }
}

internal sealed record WorktreeCheckoutsResponse(IReadOnlyList<WorktreeCheckout> Checkouts, int ArchivedDays);

internal sealed record CleanWorktreesRequest(IReadOnlyList<string> WorkspaceIds);

#pragma warning restore IL2026
