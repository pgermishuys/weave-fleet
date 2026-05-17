using WeaveFleet.Api;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class FleetEndpoints
{
    public static IEndpointRouteBuilder MapFleetSummaryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Fleet");

        group.MapGet("/fleet/summary", async (SessionService sessionService) =>
        {
            var result = await sessionService.GetFleetSummaryAsync();
            return result.Match(
                summary => Results.Ok(new FleetSummaryResponse(
                    summary.ActiveSessions,
                    summary.IdleSessions,
                    summary.TotalTokens,
                    summary.TotalCost,
                    summary.QueuedTasks)),
                error => Results.Problem(error.Description));
        })
        .Produces<FleetSummaryResponse>(200)
        .WithName("GetFleetSummary");

        // GET /api/version — assembly version + embedded source revision
        group.MapGet("/version", () => Results.Ok(new VersionResponse(
            FleetInstrumentation.ServiceVersion,
            FleetInstrumentation.ServiceCommit)))
        .WithName("GetVersion");

        // GET /api/profile — active profile name (from env or default)
        group.MapGet("/profile", () => Results.Ok(new ProfileResponse(
            Environment.GetEnvironmentVariable("WEAVE_PROFILE") ?? "default")))
        .WithName("GetProfile");

        // GET /api/repositories — scanned repos from workspace roots
        group.MapGet("/repositories", async (
            RepositoryService repoService,
            WorkspaceRootService workspaceRootService,
            CancellationToken ct) =>
        {
            var repos = await repoService.ScanRepositoriesAsync(ct);
            var allowedRoots = await workspaceRootService.GetAllowedRootsAsync();
            return Results.Ok(new RepositoriesListResponse(
                repos.Select(r => new RepositoryListItem(
                    r.Path,
                    r.Name,
                    FindParentRoot(r.Path, allowedRoots))).ToList(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        })
        .WithName("GetRepositories");

        // GET /api/repositories/info?path= — single repo metadata
        group.MapGet("/repositories/info", async (
            string path,
            RepositoryService repoService,
            CancellationToken ct) =>
        {
            var resolvedPath = await repoService.ResolveRepositoryPathAsync(path, ct);
            if (resolvedPath.IsFailure)
                return Results.BadRequest(new ErrorResponse(resolvedPath.Error.Description));

            var info = await repoService.GetRepositoryInfoAsync(resolvedPath.Value, ct);
            if (info is null)
                return Results.NotFound(new ErrorResponse("Not a git repository."));

            return Results.Ok(new RepositoryInfoResponse(
                new RepositoryInfoDto(
                    info.Name,
                    info.Path,
                    string.IsNullOrWhiteSpace(info.CurrentBranch) ? null : info.CurrentBranch,
                    string.IsNullOrWhiteSpace(info.LastCommitMessage)
                        ? null
                        : new RepositoryLastCommit(string.Empty, info.LastCommitMessage, string.Empty, string.Empty),
                    string.IsNullOrWhiteSpace(info.RemoteUrl)
                        ? Array.Empty<RepositoryRemote>()
                        : [new RepositoryRemote("origin", info.RemoteUrl)])));
        })
        .WithName("GetRepositoryInfo");

        // GET /api/repositories/worktrees?path= — list linked worktrees for a repository
        group.MapGet("/repositories/worktrees", async (
            string path,
            RepositoryService repoService,
            CancellationToken ct) =>
        {
            var resolvedPath = await repoService.ResolveRepositoryPathAsync(path, ct);
            if (resolvedPath.IsFailure)
                return Results.BadRequest(new ErrorResponse(resolvedPath.Error.Description));

            var worktrees = await repoService.ListWorktreesAsync(resolvedPath.Value, ct);
            return Results.Ok(new RepositoryWorktreesResponse(
                worktrees.Select(w => new WorktreeItem(w.Path, w.Branch, w.CommitHash)).ToList()));
        })
        .WithName("GetRepositoryWorktrees");

        // GET /api/repositories/detail?path= — enriched repo detail
        group.MapGet("/repositories/detail", async (
            string path,
            RepositoryService repoService,
            CancellationToken ct) =>
        {
            var resolvedPath = await repoService.ResolveRepositoryPathAsync(path, ct);
            if (resolvedPath.IsFailure)
                return Results.BadRequest(new ErrorResponse(resolvedPath.Error.Description));

            var detail = await repoService.GetRepositoryDetailAsync(resolvedPath.Value, ct);
            if (detail is null)
                return Results.NotFound(new ErrorResponse("Not a git repository."));

            return Results.Ok(new RepositoryDetailResponse(
                new RepositoryDetailDto(
                    detail.Info.Name,
                    detail.Info.Path,
                    string.IsNullOrWhiteSpace(detail.Info.CurrentBranch) ? null : detail.Info.CurrentBranch,
                    0,
                    0,
                    null,
                    null,
                    detail.Branches.Select(branch => new RepositoryBranchItem(
                        branch,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Equals(branch.TrimStart('*', ' '), detail.Info.CurrentBranch, StringComparison.Ordinal),
                        branch.Contains("remotes/", StringComparison.Ordinal))).ToList(),
                    Array.Empty<string>(),
                    detail.RecentCommits.Select(commit => new RepositoryCommitItem(
                        string.Empty,
                        commit.Split(' ', 2, StringSplitOptions.TrimEntries)[0],
                        commit.Contains(' ') ? commit[(commit.IndexOf(' ') + 1)..] : commit,
                        string.Empty,
                        string.Empty,
                        string.Empty)).ToList(),
                    detail.Remotes.Select(remote => new RepositoryRemoteItem(
                        ParseRemoteName(remote),
                        ParseRemoteUrl(remote),
                        null)).ToList(),
                    null,
                    null)));
        })
        .WithName("GetRepositoryDetail");

        // POST /api/repositories/refresh — invalidate cache
        group.MapPost("/repositories/refresh", async (
            RepositoryService repoService,
            WorkspaceRootService workspaceRootService,
            CancellationToken ct) =>
        {
            await repoService.RefreshScanAsync(ct);
            var repos = await repoService.ScanRepositoriesAsync(ct);
            var allowedRoots = await workspaceRootService.GetAllowedRootsAsync();
            return Results.Ok(new RepositoriesListResponse(
                repos.Select(r => new RepositoryListItem(
                    r.Path,
                    r.Name,
                    FindParentRoot(r.Path, allowedRoots))).ToList(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        })
        .WithName("RefreshRepositories");

        // GET /api/integrations — returns integration statuses
        group.MapGet("/integrations", async (IPluginCatalog pluginCatalog, CancellationToken ct) =>
        {
            var descriptors = await pluginCatalog.GetDescriptorsAsync(ct).ConfigureAwait(false);
            var statuses = await pluginCatalog.GetStatusesAsync(ct).ConfigureAwait(false);

            var statusById = statuses.ToDictionary(status => status.PluginId, StringComparer.Ordinal);

            return Results.Ok(new IntegrationsResponse(
                descriptors.Select(descriptor => new IntegrationItem(
                    descriptor.Id,
                    descriptor.DisplayName,
                    statusById.TryGetValue(descriptor.Id, out var status)
                        ? status.Status switch
                        {
                            PluginConnectionStatus.Connected => "connected",
                            PluginConnectionStatus.Error => "error",
                            _ => "disconnected",
                        }
                        : "disconnected",
                    statusById.TryGetValue(descriptor.Id, out status)
                        ? status.ConnectedAt
                        : null)).ToList()));
        })
        .WithName("GetIntegrations");

        // GET /api/available-tools — detect installed editors, terminals, explorers
        group.MapGet("/available-tools", async (ToolDetector detector, CancellationToken ct) =>
        {
            var tools = await detector.DetectAsync(ct);
            return Results.Ok(new AvailableToolsResponse(tools));
        })
        .WithName("GetAvailableTools");

        return app;
    }

    private static string FindParentRoot(string repositoryPath, IReadOnlyList<string> allowedRoots)
    {
        var normalizedRepositoryPath = Path.GetFullPath(repositoryPath);
        return allowedRoots
            .Select(Path.GetFullPath)
            .Where(root => WorkspaceRootService.IsPathWithinRoots(normalizedRepositoryPath, [root]))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault() ?? Path.GetDirectoryName(normalizedRepositoryPath) ?? normalizedRepositoryPath;
    }

    private static string ParseRemoteName(string remote)
    {
        var parts = remote.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? parts[0] : string.Empty;
    }

    private static string ParseRemoteUrl(string remote)
    {
        var parts = remote.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 1 ? parts[1] : string.Empty;
    }
}

internal sealed record AvailableToolsResponse(IReadOnlyList<ResolvedTool> Tools);

#pragma warning restore IL2026
