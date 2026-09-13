using WeaveFleet.Application.Services;
using WeaveFleet.Application.Skills;
using WeaveFleet.Application.Tools;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Skills;
using WeaveFleet.Domain.Tools;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Manifest-aware tool management endpoints.
/// </summary>
public static class ToolEndpoints
{
    public static IEndpointRouteBuilder MapToolEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tools").WithTags("Tools");

        // GET /api/tools — list from manifest, with where each tool is installed
        group.MapGet("", async (
            IToolManifestStore manifestStore,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            var manifest = await manifestStore.LoadAsync(userContext.UserId, workspaceId: null, ct);

            var response = new ToolListResponse(
                Tools: manifest.Tools.Select(e => new ToolDto(
                    Name: e.Name,
                    ToolType: e.ToolType.ToString().ToLowerInvariant(),
                    DisplayName: null,
                    Description: null,
                    Command: e.Command,
                    Args: e.Args,
                    Env: e.Env,
                    RepoUrl: e.RepoUrl,
                    LocalPath: e.LocalPath,
                    Scope: InstallTargets.ToApi(e.Scope),
                    ProjectPath: e.ProjectPath,
                    InstalledPath: e.InstalledPath,
                    InstalledAt: e.InstalledAt,
                    UpdatedAt: e.UpdatedAt
                )).ToArray()
            );

            return Results.Ok(response);
        })
        .WithName("ListTools")
        .Produces<ToolListResponse>();

        // GET /api/tools/catalog — return catalog entries
        group.MapGet("/catalog", async (
            IToolCatalogService catalogService,
            CancellationToken ct) =>
        {
            var result = await catalogService.FetchCatalogAsync(ct);

            return result.Match(
                onSuccess: catalog => Results.Ok(new ToolCatalogResponse(
                    Entries: catalog.Entries.Select(e => new ToolCatalogDto(
                        Name: e.Name,
                        ToolType: e.ToolType.ToString().ToLowerInvariant(),
                        Source: (int)e.Source,
                        DisplayName: e.DisplayName,
                        Description: e.Description,
                        Command: e.Command,
                        Args: e.Args,
                        Env: e.Env,
                        RepoUrl: e.RepoUrl,
                        Ref: e.Ref,
                        SubPath: e.SubPath,
                        LocalPath: e.LocalPath,
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
        .WithName("GetToolCatalog")
        .Produces<ToolCatalogResponse>()
        .ProducesProblem(500);

        // POST /api/tools/install — install where OpenCode loads it (globally or into a repository), add to manifest
        group.MapPost("/install", async (
            InstallToolRequest req,
            IToolManifestStore manifestStore,
            IToolInstaller installer,
            IGitHubSkillFetcher gitHubFetcher,
            RepositoryService repositories,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new ErrorResponse("Tool name is required."));

            if (!IsValidToolName(req.Name))
                return Results.BadRequest(new ErrorResponse("Invalid tool name."));

            var target = await InstallTargets.ResolveAsync(req.Scope, req.ProjectPath, repositories, ct);
            if (target.IsFailure)
                return InstallTargets.ToErrorResult(target.Error, "Invalid install target");

            // A tool can be installed once per target: globally, and into any number of repositories.
            var manifest = await manifestStore.LoadAsync(userContext.UserId, workspaceId: null, ct);
            if (FindEntry(manifest, req.Name, target.Value) is not null)
                return Results.Conflict(new ErrorResponse($"Tool '{req.Name}' is already installed there."));

            // Parse tool type
            if (!Enum.TryParse<ToolType>(req.ToolType, ignoreCase: true, out var toolType))
                return Results.BadRequest(new ErrorResponse($"Invalid tool type '{req.ToolType}'. Must be 'native' or 'mcp'."));

            // Validate local path for native tools
            var sourcePath = req.LocalPath;
            if (toolType == ToolType.Native)
            {
                if (req.Source == SkillSource.GitHub)
                {
                    // Clone from GitHub to get source files
                    if (string.IsNullOrWhiteSpace(req.RepoUrl))
                        return Results.BadRequest(new ErrorResponse("RepoUrl is required for GitHub source."));

                    var cloneResult = await gitHubFetcher.CloneOrUpdateAsync(
                        req.RepoUrl,
                        $"tool-{req.Name}",
                        req.Ref,
                        req.SubPath,
                        ct);

                    if (cloneResult.IsFailure)
                        return Results.Problem(
                            statusCode: 500,
                            title: "GitHub clone failed",
                            detail: cloneResult.Error.Description);

                    sourcePath = cloneResult.Value;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(req.LocalPath))
                        return Results.BadRequest(new ErrorResponse("LocalPath is required for native tools."));

                    if (!File.Exists(req.LocalPath) && !Directory.Exists(req.LocalPath))
                        return Results.BadRequest(new ErrorResponse("Local file does not exist."));
                }
            }

            // Validate MCP fields for MCP tools
            if (toolType == ToolType.Mcp)
            {
                if (string.IsNullOrWhiteSpace(req.Command))
                    return Results.BadRequest(new ErrorResponse("Command is required for MCP tools."));
            }

            // Install tool (branches on ToolType)
            Result<string> installResult = toolType switch
            {
                ToolType.Native => await installer.InstallNativeAsync(req.Name, sourcePath!, target.Value, ct),
                ToolType.Mcp => await installer.InstallMcpAsync(req.Name, req.Command!, req.Args, req.Env, target.Value, ct),
                _ => Result.Failure<string>(new FleetError("Tool.InvalidType", "Invalid tool type"))
            };

            if (installResult.IsFailure)
                return InstallTargets.ToErrorResult(installResult.Error, "Tool installation failed");

            var now = DateTimeOffset.UtcNow;
            var entry = new ToolManifestEntry
            {
                Name = req.Name,
                ToolType = toolType,
                Source = req.Source,
                Command = req.Command,
                Args = req.Args,
                Env = req.Env,
                RepoUrl = req.RepoUrl,
                LocalPath = req.LocalPath,
                Scope = target.Value.Scope,
                ProjectPath = target.Value.ProjectPath,
                InstalledPath = installResult.Value,
                InstalledAt = now,
                UpdatedAt = now
            };

            await manifestStore.AddEntryAsync(userContext.UserId, workspaceId: null, entry, ct);

            return Results.Created(
                $"/api/tools/{req.Name}",
                new InstallToolResponse(
                    Name: req.Name,
                    ToolType: toolType.ToString().ToLowerInvariant(),
                    InstalledPath: installResult.Value,
                    InstalledAt: entry.InstalledAt
                )
            );
        })
        .WithName("InstallTool")
        .Produces<InstallToolResponse>(201)
        .ProducesProblem(400)
        .ProducesProblem(409)
        .ProducesProblem(500);

        // DELETE /api/tools/{name}?scope=&projectPath= — delete what Fleet installed, then the manifest entry
        group.MapDelete("/{name}", async (
            string name,
            string? scope,
            string? projectPath,
            IToolManifestStore manifestStore,
            IToolInstaller installer,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            if (!IsValidToolName(name))
                return Results.BadRequest(new ErrorResponse("Invalid tool name."));

            var target = InstallTargets.Parse(scope, projectPath);
            if (target.IsFailure)
                return InstallTargets.ToErrorResult(target.Error, "Invalid install target");

            var manifest = await manifestStore.LoadAsync(userContext.UserId, workspaceId: null, ct);
            var entry = FindEntry(manifest, name, target.Value);

            if (entry is null)
                return Results.NotFound(new ErrorResponse($"Tool '{name}' not found."));

            // Keep the entry when the files couldn't be removed, so the user can retry.
            var uninstallResult = await installer.UninstallAsync(entry, ct);
            if (uninstallResult.IsFailure)
                return InstallTargets.ToErrorResult(uninstallResult.Error, "Tool removal failed");

            await manifestStore.RemoveEntryAsync(userContext.UserId, workspaceId: null, name, target.Value, ct);

            return Results.NoContent();
        })
        .WithName("DeleteTool")
        .Produces(204)
        .ProducesProblem(400)
        .ProducesProblem(404)
        .ProducesProblem(500);

        return app;
    }

    private static ToolManifestEntry? FindEntry(ToolManifest manifest, string name, InstallTarget target) =>
        manifest.Tools.FirstOrDefault(t => t.Name == name && target.Matches(t.Scope, t.ProjectPath));

    /// <summary>
    /// Returns true when <paramref name="name"/> is a valid tool name:
    /// non-empty, not "." or "..", and contains no path separators.
    /// This is an early-out check before Path.Combine is called.
    /// </summary>
    internal static bool IsValidToolName(string name)
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

internal sealed record InstallToolRequest(
    string Name,
    string ToolType,
    WeaveFleet.Domain.Skills.SkillSource Source,
    string? Command,
    IReadOnlyList<string>? Args,
    IReadOnlyDictionary<string, string>? Env,
    string? RepoUrl,
    string? Ref,
    string? SubPath,
    string? LocalPath,
    string? Scope = null,
    string? ProjectPath = null);

internal sealed record InstallToolResponse(
    string Name,
    string ToolType,
    string InstalledPath,
    DateTimeOffset InstalledAt);

internal sealed record ToolListResponse(
    IReadOnlyList<ToolDto> Tools);

internal sealed record ToolDto(
    string Name,
    string ToolType,
    string? DisplayName,
    string? Description,
    string? Command,
    IReadOnlyList<string>? Args,
    IReadOnlyDictionary<string, string>? Env,
    string? RepoUrl,
    string? LocalPath,
    string Scope,
    string? ProjectPath,
    string? InstalledPath,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt);

internal sealed record ToolCatalogResponse(
    IReadOnlyList<ToolCatalogDto> Entries,
    bool IsStale,
    DateTimeOffset? CachedAt);

internal sealed record ToolCatalogDto(
    string Name,
    string ToolType,
    int Source,
    string? DisplayName,
    string? Description,
    string? Command,
    IReadOnlyList<string>? Args,
    IReadOnlyDictionary<string, string>? Env,
    string? RepoUrl,
    string? Ref,
    string? SubPath,
    string? LocalPath,
    string? Author,
    string? Version,
    IReadOnlyList<string> Tags,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);

#pragma warning restore IL2026
