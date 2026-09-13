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

        // GET /api/skills — list from manifest, with where each skill is installed
        group.MapGet("", async (
            ISkillManifestStore manifestStore,
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
                    Scope: InstallTargets.ToApi(e.Scope),
                    ProjectPath: e.ProjectPath,
                    InstalledPaths: e.InstalledPaths,
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

        // POST /api/skills/install — fetch the skill, copy it into each harness at the chosen scope, add to manifest
        group.MapPost("/install", async (
            InstallSkillRequest req,
            ISkillManifestStore manifestStore,
            IGitHubSkillFetcher gitHubFetcher,
            ISkillSyncEngine syncEngine,
            RepositoryService repositories,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new ErrorResponse("Skill name is required."));

            if (!IsValidSkillName(req.Name))
                return Results.BadRequest(new ErrorResponse("Invalid skill name."));

            var target = await InstallTargets.ResolveAsync(req.Scope, req.ProjectPath, repositories, ct);
            if (target.IsFailure)
                return InstallTargets.ToErrorResult(target.Error, "Invalid install target");

            // A skill can be installed once per target: globally, and into any number of repositories.
            var manifest = await manifestStore.LoadAsync(userContext.UserId, workspaceId: null, ct);
            if (FindEntry(manifest, req.Name, target.Value) is not null)
                return Results.Conflict(new ErrorResponse($"Skill '{req.Name}' is already installed there."));

            // Validate source
            var localPath = req.LocalPath;
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

                // Use the local path where the skill was cloned
                localPath = cloneResult.Value;
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

            var now = DateTimeOffset.UtcNow;
            var entry = new SkillManifestEntry
            {
                Name = req.Name,
                Source = req.Source,
                RepoUrl = req.RepoUrl,
                Ref = req.Ref,
                SubPath = req.SubPath,
                LocalPath = localPath,
                TargetHarnesses = req.TargetHarnesses is { Count: > 0 } ? req.TargetHarnesses : [DefaultHarness],
                Scope = target.Value.Scope,
                ProjectPath = target.Value.ProjectPath,
                InstalledAt = now,
                UpdatedAt = now
            };

            var syncResults = await syncEngine.SyncSkillAsync(entry, ct);

            // Nothing landed where a harness looks: don't record an install that isn't there.
            if (!syncResults.Any(r => r.Success))
            {
                var detail = string.Join(" ", syncResults.Select(r => r.ErrorMessage).Where(m => m is not null));
                return syncResults.Any(r => r.Skipped)
                    ? Results.Conflict(new ErrorResponse(detail))
                    : Results.Problem(statusCode: 500, title: "Skill installation failed", detail: detail);
            }

            await manifestStore.AddEntryAsync(userContext.UserId, workspaceId: null, entry.WithSyncedPaths(syncResults), ct);

            return Results.Created(
                $"/api/skills/{req.Name}",
                new InstallSkillResponse(
                    Name: req.Name,
                    SyncResults: ToDtos(syncResults)
                )
            );
        })
        .WithName("InstallSkill")
        .Produces<InstallSkillResponse>(201)
        .ProducesProblem(400)
        .ProducesProblem(409)
        .ProducesProblem(500);

        // POST /api/skills/{name}/update?scope=&projectPath= — pull latest, copy it over the installed skill
        group.MapPost("/{name}/update", async (
            string name,
            string? scope,
            string? projectPath,
            ISkillManifestStore manifestStore,
            IGitHubSkillFetcher gitHubFetcher,
            ISkillSyncEngine syncEngine,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            if (!IsValidSkillName(name))
                return Results.BadRequest(new ErrorResponse("Invalid skill name."));

            var target = InstallTargets.Parse(scope, projectPath);
            if (target.IsFailure)
                return InstallTargets.ToErrorResult(target.Error, "Invalid install target");

            var manifest = await manifestStore.LoadAsync(userContext.UserId, workspaceId: null, ct);
            var entry = FindEntry(manifest, name, target.Value);

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

            // Copy the fresh files over the installed ones, then record the new timestamp and paths
            var syncResults = await syncEngine.SyncSkillAsync(entry, ct);
            var updatedEntry = entry.WithSyncedPaths(syncResults) with { UpdatedAt = DateTimeOffset.UtcNow };
            await manifestStore.UpdateEntryAsync(userContext.UserId, workspaceId: null, updatedEntry, ct);

            return Results.Ok(new UpdateSkillResponse(
                Name: name,
                UpdatedAt: updatedEntry.UpdatedAt,
                SyncResults: ToDtos(syncResults)
            ));
        })
        .WithName("UpdateSkill")
        .Produces<UpdateSkillResponse>()
        .ProducesProblem(400)
        .ProducesProblem(404)
        .ProducesProblem(500);

        // DELETE /api/skills/{name}?scope=&projectPath= — delete the skill folders Fleet installed, then the manifest entry
        group.MapDelete("/{name}", async (
            string name,
            string? scope,
            string? projectPath,
            ISkillManifestStore manifestStore,
            ISkillSyncEngine syncEngine,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            if (!IsValidSkillName(name))
                return Results.BadRequest(new ErrorResponse("Invalid skill name."));

            var target = InstallTargets.Parse(scope, projectPath);
            if (target.IsFailure)
                return InstallTargets.ToErrorResult(target.Error, "Invalid install target");

            var manifest = await manifestStore.LoadAsync(userContext.UserId, workspaceId: null, ct);
            var entry = FindEntry(manifest, name, target.Value);

            if (entry is null)
                return Results.NotFound(new ErrorResponse($"Skill '{name}' not found."));

            if (entry.Source == SkillSource.Bundled)
                return Results.BadRequest(new ErrorResponse("Bundled skills cannot be removed."));

            // Keep the entry when a folder couldn't be deleted, so the user can retry.
            var removeResults = await syncEngine.RemoveSkillAsync(entry, ct);
            var failures = removeResults.Where(r => !r.Success).Select(r => r.ErrorMessage).ToArray();
            if (failures.Length > 0)
                return Results.Problem(statusCode: 500, title: "Skill removal failed", detail: string.Join(" ", failures));

            await manifestStore.RemoveEntryAsync(userContext.UserId, workspaceId: null, name, target.Value, ct);

            return Results.NoContent();
        })
        .WithName("DeleteSkill")
        .Produces(204)
        .ProducesProblem(400)
        .ProducesProblem(404)
        .ProducesProblem(500);

        // GET /api/skills/{name}/update-check?scope=&projectPath= — check if update available
        group.MapGet("/{name}/update-check", async (
            string name,
            string? scope,
            string? projectPath,
            ISkillManifestStore manifestStore,
            IGitHubSkillFetcher gitHubFetcher,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            if (!IsValidSkillName(name))
                return Results.BadRequest(new ErrorResponse("Invalid skill name."));

            var target = InstallTargets.Parse(scope, projectPath);
            if (target.IsFailure)
                return InstallTargets.ToErrorResult(target.Error, "Invalid install target");

            var manifest = await manifestStore.LoadAsync(userContext.UserId, workspaceId: null, ct);
            var entry = FindEntry(manifest, name, target.Value);

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

    // OpenCode is the harness Fleet runs; a skill that names none is installed for it.
    private const string DefaultHarness = "opencode";

    private static SkillManifestEntry? FindEntry(SkillManifest manifest, string name, InstallTarget target) =>
        manifest.Skills.FirstOrDefault(s => s.Name == name && target.Matches(s.Scope, s.ProjectPath));

    private static SkillSyncResultDto[] ToDtos(IEnumerable<SkillSyncResult> results) =>
        results.Select(r => new SkillSyncResultDto(
            SkillName: r.SkillName,
            Harness: r.Harness,
            Success: r.Success,
            Skipped: r.Skipped,
            ErrorMessage: r.ErrorMessage,
            TargetPath: r.TargetPath
        )).ToArray();

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
    IReadOnlyList<string>? TargetHarnesses,
    string? Scope = null,
    string? ProjectPath = null);

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
    string Scope,
    string? ProjectPath,
    IReadOnlyList<string> InstalledPaths,
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
