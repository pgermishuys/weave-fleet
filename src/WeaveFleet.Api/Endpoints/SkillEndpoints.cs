using WeaveFleet.Application.Services;
using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Skills;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Manifest-aware skill management endpoints.
/// </summary>
public static class SkillEndpoints
{
    public static IEndpointRouteBuilder MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/skills").WithTags("Skills");

        // GET /api/skills — list from manifest (with sync status per harness)
        group.MapGet("", async (
            ISkillManifestStore manifestStore,
            ISkillSyncEngine syncEngine,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            var manifest = await manifestStore.LoadAsync(userContext.UserId, workspaceId: null, ct);

            var response = new SkillListResponse(
                Skills: manifest.Skills.Select(e => new SkillListItemDto(
                    Name: e.Name,
                    Source: e.Source,
                    RepoUrl: e.RepoUrl,
                    Ref: e.Ref,
                    SubPath: e.SubPath,
                    LocalPath: e.LocalPath,
                    TargetHarnesses: e.TargetHarnesses,
                    InstalledAt: e.InstalledAt,
                    UpdatedAt: e.UpdatedAt
                )).ToArray()
            );

            return Results.Ok(response);
        })
        .WithName("ListSkills")
        .Produces<SkillListResponse>();

        // GET /api/skills/catalog — return catalog entries
        group.MapGet("/catalog", async (
            ISkillCatalogService catalogService,
            CancellationToken ct) =>
        {
            var result = await catalogService.FetchCatalogAsync(ct);

            return result.Match(
                onSuccess: catalog => Results.Ok(new SkillCatalogResponse(
                    Entries: catalog.Entries.Select(e => new CatalogEntryDto(
                        Name: e.Name,
                        DisplayName: e.DisplayName,
                        Description: e.Description,
                        Source: e.Source,
                        RepoUrl: e.RepoUrl,
                        Ref: e.Ref,
                        SubPath: e.SubPath,
                        LocalPath: e.LocalPath,
                        TargetHarnesses: e.TargetHarnesses,
                        Author: e.Author,
                        Version: e.Version,
                        Tags: e.Tags,
                        CreatedAt: e.CreatedAt,
                        UpdatedAt: e.UpdatedAt
                    )).ToArray(),
                    IsStale: catalog.IsStale,
                    CachedAt: catalog.CachedAt
                )),
                onFailure: error => Results.Problem(
                    statusCode: 500,
                    title: "Catalog fetch failed",
                    detail: error.Description
                )
            );
        })
        .WithName("GetSkillCatalog")
        .Produces<SkillCatalogResponse>()
        .ProducesProblem(500);

        // POST /api/skills/install — install from catalog/URL/local, add to manifest, sync
        group.MapPost("/install", async (
            InstallSkillRequest req,
            ISkillManifestStore manifestStore,
            IGitHubSkillFetcher gitHubFetcher,
            ISkillSyncEngine syncEngine,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new ErrorResponse("Skill name is required."));

            if (!IsValidSkillName(req.Name))
                return Results.BadRequest(new ErrorResponse("Invalid skill name."));

            // Check if skill already exists
            var manifest = await manifestStore.LoadAsync(userContext.UserId, workspaceId: null, ct);
            if (manifest.Skills.Any(s => s.Name == req.Name))
                return Results.Conflict(new ErrorResponse($"Skill '{req.Name}' is already installed."));

            // Validate source
            if (req.Source == SkillSource.GitHub)
            {
                if (string.IsNullOrWhiteSpace(req.RepoUrl))
                    return Results.BadRequest(new ErrorResponse("RepoUrl is required for GitHub source."));

                // Clone or update from GitHub
                var cloneResult = await gitHubFetcher.CloneOrUpdateAsync(
                    req.RepoUrl,
                    req.Name,
                    req.Ref,
                    req.SubPath,
                    ct);

                if (cloneResult.IsFailure)
                    return Results.Problem(
                        statusCode: 500,
                        title: "GitHub clone failed",
                        detail: cloneResult.Error.Description
                    );
            }
            else if (req.Source == SkillSource.Local)
            {
                if (string.IsNullOrWhiteSpace(req.LocalPath))
                    return Results.BadRequest(new ErrorResponse("LocalPath is required for Local source."));

                if (!Directory.Exists(req.LocalPath))
                    return Results.BadRequest(new ErrorResponse("Local path does not exist."));
            }
            else if (req.Source == SkillSource.Bundled)
            {
                return Results.BadRequest(new ErrorResponse("Bundled skills cannot be installed via API."));
            }

            // Add to manifest
            var entry = new SkillManifestEntry
            {
                Name = req.Name,
                Source = req.Source,
                RepoUrl = req.RepoUrl,
                Ref = req.Ref,
                SubPath = req.SubPath,
                LocalPath = req.LocalPath,
                TargetHarnesses = req.TargetHarnesses ?? [],
                InstalledAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await manifestStore.AddEntryAsync(userContext.UserId, workspaceId: null, entry, ct);

            // Sync to harnesses
            var syncResults = await syncEngine.SyncSkillAsync(req.Name, ct);

            return Results.Created(
                $"/api/skills/{req.Name}",
                new InstallSkillResponse(
                    Name: req.Name,
                    SyncResults: syncResults.Select(r => new SkillSyncResultDto(
                        SkillName: r.SkillName,
                        Harness: r.Harness,
                        Success: r.Success,
                        Skipped: r.Skipped,
                        ErrorMessage: r.ErrorMessage,
                        TargetPath: r.TargetPath
                    )).ToArray()
                )
            );
        })
        .WithName("InstallSkill")
        .Produces<InstallSkillResponse>(201)
        .ProducesProblem(400)
        .ProducesProblem(409)
        .ProducesProblem(500);

        // POST /api/skills/{name}/update — pull latest, update manifest, sync
        group.MapPost("/{name}/update", async (
            string name,
            ISkillManifestStore manifestStore,
            IGitHubSkillFetcher gitHubFetcher,
            ISkillSyncEngine syncEngine,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            if (!IsValidSkillName(name))
                return Results.BadRequest(new ErrorResponse("Invalid skill name."));

            var manifest = await manifestStore.LoadAsync(userContext.UserId, workspaceId: null, ct);
            var entry = manifest.Skills.FirstOrDefault(s => s.Name == name);

            if (entry is null)
                return Results.NotFound(new ErrorResponse($"Skill '{name}' not found."));

            if (entry.Source != SkillSource.GitHub)
                return Results.BadRequest(new ErrorResponse("Only GitHub skills can be updated."));

            // Pull latest from GitHub
            var updateResult = await gitHubFetcher.CloneOrUpdateAsync(
                entry.RepoUrl!,
                entry.Name,
                entry.Ref,
                entry.SubPath,
                ct);

            if (updateResult.IsFailure)
                return Results.Problem(
                    statusCode: 500,
                    title: "GitHub update failed",
                    detail: updateResult.Error.Description
                );

            // Update manifest timestamp
            var updatedEntry = entry with { UpdatedAt = DateTimeOffset.UtcNow };
            await manifestStore.UpdateEntryAsync(userContext.UserId, workspaceId: null, updatedEntry, ct);

            // Sync to harnesses
            var syncResults = await syncEngine.SyncSkillAsync(name, ct);

            return Results.Ok(new UpdateSkillResponse(
                Name: name,
                UpdatedAt: updatedEntry.UpdatedAt,
                SyncResults: syncResults.Select(r => new SkillSyncResultDto(
                    SkillName: r.SkillName,
                    Harness: r.Harness,
                    Success: r.Success,
                    Skipped: r.Skipped,
                    ErrorMessage: r.ErrorMessage,
                    TargetPath: r.TargetPath
                )).ToArray()
            ));
        })
        .WithName("UpdateSkill")
        .Produces<UpdateSkillResponse>()
        .ProducesProblem(400)
        .ProducesProblem(404)
        .ProducesProblem(500);

        // DELETE /api/skills/{name} — remove from manifest, remove symlinks, sync
        group.MapDelete("/{name}", async (
            string name,
            ISkillManifestStore manifestStore,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            if (!IsValidSkillName(name))
                return Results.BadRequest(new ErrorResponse("Invalid skill name."));

            var manifest = await manifestStore.LoadAsync(userContext.UserId, workspaceId: null, ct);
            var entry = manifest.Skills.FirstOrDefault(s => s.Name == name);

            if (entry is null)
                return Results.NotFound(new ErrorResponse($"Skill '{name}' not found."));

            if (entry.Source == SkillSource.Bundled)
                return Results.BadRequest(new ErrorResponse("Bundled skills cannot be removed."));

            // Remove from manifest
            await manifestStore.RemoveEntryAsync(userContext.UserId, workspaceId: null, name, ct);

            // Note: Sync engine will handle symlink removal on next sync
            // For immediate cleanup, we could trigger a full sync here, but that's expensive
            // Instead, we rely on the sync engine to clean up stale symlinks

            return Results.NoContent();
        })
        .WithName("DeleteSkill")
        .Produces(204)
        .ProducesProblem(400)
        .ProducesProblem(404);

        // GET /api/skills/{name}/update-check — check if update available
        group.MapGet("/{name}/update-check", async (
            string name,
            ISkillManifestStore manifestStore,
            IGitHubSkillFetcher gitHubFetcher,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            if (!IsValidSkillName(name))
                return Results.BadRequest(new ErrorResponse("Invalid skill name."));

            var manifest = await manifestStore.LoadAsync(userContext.UserId, workspaceId: null, ct);
            var entry = manifest.Skills.FirstOrDefault(s => s.Name == name);

            if (entry is null)
                return Results.NotFound(new ErrorResponse($"Skill '{name}' not found."));

            if (entry.Source != SkillSource.GitHub)
                return Results.Ok(new UpdateCheckResponse(
                    Name: name,
                    UpdateAvailable: false,
                    RemoteRef: null,
                    LocalRef: null,
                    Message: "Only GitHub skills support update checks."
                ));

            var checkResult = await gitHubFetcher.CheckForUpdateAsync(entry, ct);

            return checkResult.Match(
                onSuccess: result => Results.Ok(new UpdateCheckResponse(
                    Name: name,
                    UpdateAvailable: result.UpdateAvailable,
                    RemoteRef: result.RemoteRef,
                    LocalRef: result.LocalRef,
                    Message: result.UpdateAvailable ? "Update available" : "Up to date"
                )),
                onFailure: error => Results.Problem(
                    statusCode: 500,
                    title: "Update check failed",
                    detail: error.Description
                )
            );
        })
        .WithName("CheckSkillUpdate")
        .Produces<UpdateCheckResponse>()
        .ProducesProblem(400)
        .ProducesProblem(404)
        .ProducesProblem(500);

        // GET /api/skills/manifest — raw manifest read
        group.MapGet("/manifest", async (
            ISkillManifestStore manifestStore,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            var manifest = await manifestStore.LoadAsync(userContext.UserId, workspaceId: null, ct);
            return Results.Ok(manifest);
        })
        .WithName("GetSkillManifest")
        .Produces<SkillManifest>();

        return app;
    }

    /// <summary>
    /// Returns true when <paramref name="name"/> is a valid skill name:
    /// non-empty, not "." or "..", and contains no path separators.
    /// This is an early-out check before Path.Combine is called.
    /// </summary>
    internal static bool IsValidSkillName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name is "." or "..")
            return false;

        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
            return false;

        return true;
    }
}

// Request/Response DTOs

internal sealed record InstallSkillRequest(
    string Name,
    SkillSource Source,
    string? RepoUrl,
    string? Ref,
    string? SubPath,
    string? LocalPath,
    IReadOnlyList<string>? TargetHarnesses);

internal sealed record InstallSkillResponse(
    string Name,
    IReadOnlyList<SkillSyncResultDto> SyncResults);

internal sealed record UpdateSkillResponse(
    string Name,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<SkillSyncResultDto> SyncResults);

internal sealed record UpdateCheckResponse(
    string Name,
    bool UpdateAvailable,
    string? RemoteRef,
    string? LocalRef,
    string? Message);

internal sealed record SkillListResponse(
    IReadOnlyList<SkillListItemDto> Skills);

internal sealed record SkillListItemDto(
    string Name,
    SkillSource Source,
    string? RepoUrl,
    string? Ref,
    string? SubPath,
    string? LocalPath,
    IReadOnlyList<string> TargetHarnesses,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt);

internal sealed record SkillCatalogResponse(
    IReadOnlyList<CatalogEntryDto> Entries,
    bool IsStale,
    DateTimeOffset? CachedAt);

internal sealed record CatalogEntryDto(
    string Name,
    string? DisplayName,
    string? Description,
    SkillSource Source,
    string? RepoUrl,
    string? Ref,
    string? SubPath,
    string? LocalPath,
    IReadOnlyList<string> TargetHarnesses,
    string? Author,
    string? Version,
    IReadOnlyList<string> Tags,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);

internal sealed record SkillSyncResultDto(
    string SkillName,
    string Harness,
    bool Success,
    bool Skipped,
    string? ErrorMessage,
    string? TargetPath);

#pragma warning restore IL2026
