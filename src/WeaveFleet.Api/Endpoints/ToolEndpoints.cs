using WeaveFleet.Application.Services;
using WeaveFleet.Application.Tools;
using WeaveFleet.Domain.Common;
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

        // GET /api/tools — list from manifest
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
                        DisplayName: e.DisplayName,
                        Description: e.Description,
                        Command: e.Command,
                        Args: e.Args,
                        Env: e.Env,
                        RepoUrl: e.RepoUrl,
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

        // POST /api/tools/install — install from catalog, add to manifest
        group.MapPost("/install", async (
            InstallToolRequest req,
            IToolManifestStore manifestStore,
            IToolInstaller installer,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new ErrorResponse("Tool name is required."));

            if (!IsValidToolName(req.Name))
                return Results.BadRequest(new ErrorResponse("Invalid tool name."));

            // Check if tool already exists
            var manifest = await manifestStore.LoadAsync(userContext.UserId, workspaceId: null, ct);
            if (manifest.Tools.Any(t => t.Name == req.Name))
                return Results.Conflict(new ErrorResponse($"Tool '{req.Name}' is already installed."));

            // Parse tool type
            if (!Enum.TryParse<ToolType>(req.ToolType, ignoreCase: true, out var toolType))
                return Results.BadRequest(new ErrorResponse($"Invalid tool type '{req.ToolType}'. Must be 'native' or 'mcp'."));

            // Validate local path for native tools
            if (toolType == ToolType.Native)
            {
                if (string.IsNullOrWhiteSpace(req.LocalPath))
                    return Results.BadRequest(new ErrorResponse("LocalPath is required for native tools."));

                if (!File.Exists(req.LocalPath))
                    return Results.BadRequest(new ErrorResponse("Local file does not exist."));
            }

            // Validate MCP fields for MCP tools
            if (toolType == ToolType.Mcp)
            {
                if (string.IsNullOrWhiteSpace(req.Command))
                    return Results.BadRequest(new ErrorResponse("Command is required for MCP tools."));
            }

            // Create manifest entry
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
                InstalledAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            // Install tool (branches on ToolType)
            Result<string> installResult = toolType switch
            {
                ToolType.Native => await installer.InstallNativeAsync(
                    req.Name,
                    req.LocalPath!,
                    userContext.UserId,
                    workspaceId: null,
                    ct),
                ToolType.Mcp => await installer.InstallMcpAsync(
                    req.Name,
                    req.Command!,
                    req.Args,
                    req.Env,
                    userContext.UserId,
                    workspaceId: null,
                    ct),
                _ => Result.Failure<string>(new FleetError("Tool.InvalidType", "Invalid tool type"))
            };

            if (installResult.IsFailure)
                return Results.Problem(
                    statusCode: 500,
                    title: "Tool installation failed",
                    detail: installResult.Error.Description
                );

            // Add to manifest
            await manifestStore.AddEntryAsync(userContext.UserId, workspaceId: null, entry, ct);

            return Results.Created(
                $"/api/tools/{req.Name}",
                new InstallToolResponse(
                    Name: req.Name,
                    ToolType: toolType.ToString().ToLowerInvariant(),
                    InstalledAt: entry.InstalledAt
                )
            );
        })
        .WithName("InstallTool")
        .Produces<InstallToolResponse>(201)
        .ProducesProblem(400)
        .ProducesProblem(409)
        .ProducesProblem(500);

        // DELETE /api/tools/{name} — remove from manifest
        group.MapDelete("/{name}", async (
            string name,
            IToolManifestStore manifestStore,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            if (!IsValidToolName(name))
                return Results.BadRequest(new ErrorResponse("Invalid tool name."));

            var manifest = await manifestStore.LoadAsync(userContext.UserId, workspaceId: null, ct);
            var entry = manifest.Tools.FirstOrDefault(t => t.Name == name);

            if (entry is null)
                return Results.NotFound(new ErrorResponse($"Tool '{name}' not found."));

            // Remove from manifest
            await manifestStore.RemoveEntryAsync(userContext.UserId, workspaceId: null, name, ct);

            return Results.NoContent();
        })
        .WithName("DeleteTool")
        .Produces(204)
        .ProducesProblem(400)
        .ProducesProblem(404);

        return app;
    }

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
    string? LocalPath);

internal sealed record InstallToolResponse(
    string Name,
    string ToolType,
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
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt);

internal sealed record ToolCatalogResponse(
    IReadOnlyList<ToolCatalogDto> Entries,
    bool IsStale,
    DateTimeOffset? CachedAt);

internal sealed record ToolCatalogDto(
    string Name,
    string ToolType,
    string? DisplayName,
    string? Description,
    string? Command,
    IReadOnlyList<string>? Args,
    IReadOnlyDictionary<string, string>? Env,
    string? RepoUrl,
    string? LocalPath,
    string? Author,
    string? Version,
    IReadOnlyList<string> Tags,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);

#pragma warning restore IL2026
