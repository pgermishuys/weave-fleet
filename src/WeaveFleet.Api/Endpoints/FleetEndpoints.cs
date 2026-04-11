using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Api.Endpoints;

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
        group.MapGet("/version", () => Results.Ok(new
        {
            version = FleetInstrumentation.ServiceVersion,
            commit = FleetInstrumentation.ServiceCommit
        }))
        .WithName("GetVersion");

        // GET /api/profile — active profile name (from env or default)
        group.MapGet("/profile", () => Results.Ok(new
        {
            profile = Environment.GetEnvironmentVariable("WEAVE_PROFILE") ?? "default"
        }))
        .WithName("GetProfile");

        // GET /api/repositories — scanned repos from workspace roots
        group.MapGet("/repositories", async (
            RepositoryService repoService,
            WorkspaceRootService workspaceRootService,
            CancellationToken ct) =>
        {
            var repos = await repoService.ScanRepositoriesAsync(ct);
            var allowedRoots = await workspaceRootService.GetAllowedRootsAsync();
            return Results.Ok(new
            {
                repositories = repos.Select(r => new
                {
                    path = r.Path,
                    name = r.Name,
                    parentRoot = FindParentRoot(r.Path, allowedRoots)
                }),
                scannedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
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
                return Results.BadRequest(new { error = resolvedPath.Error.Description });

            var info = await repoService.GetRepositoryInfoAsync(resolvedPath.Value, ct);
            if (info is null)
                return Results.NotFound(new { error = "Not a git repository." });

            return Results.Ok(new
            {
                repository = new
                {
                    name = info.Name,
                    path = info.Path,
                    branch = string.IsNullOrWhiteSpace(info.CurrentBranch) ? null : info.CurrentBranch,
                    lastCommit = string.IsNullOrWhiteSpace(info.LastCommitMessage)
                        ? null
                        : new
                        {
                            hash = string.Empty,
                            message = info.LastCommitMessage,
                            author = string.Empty,
                            date = string.Empty
                        },
                    remotes = string.IsNullOrWhiteSpace(info.RemoteUrl)
                        ? Array.Empty<object>()
                        : new object[]
                        {
                            new { name = "origin", url = info.RemoteUrl }
                        }
                }
            });
        })
        .WithName("GetRepositoryInfo");

        // GET /api/repositories/detail?path= — enriched repo detail
        group.MapGet("/repositories/detail", async (
            string path,
            RepositoryService repoService,
            CancellationToken ct) =>
        {
            var resolvedPath = await repoService.ResolveRepositoryPathAsync(path, ct);
            if (resolvedPath.IsFailure)
                return Results.BadRequest(new { error = resolvedPath.Error.Description });

            var detail = await repoService.GetRepositoryDetailAsync(resolvedPath.Value, ct);
            if (detail is null)
                return Results.NotFound(new { error = "Not a git repository." });

            return Results.Ok(new
            {
                repository = new
                {
                    name = detail.Info.Name,
                    path = detail.Info.Path,
                    branch = string.IsNullOrWhiteSpace(detail.Info.CurrentBranch) ? null : detail.Info.CurrentBranch,
                    uncommittedCount = 0,
                    totalCommitCount = 0,
                    firstCommitDate = (string?)null,
                    lastCommitDate = (string?)null,
                    branches = detail.Branches.Select(branch => new
                    {
                        name = branch,
                        shortHash = string.Empty,
                        message = string.Empty,
                        author = string.Empty,
                        authorEmail = string.Empty,
                        date = string.Empty,
                        isCurrent = string.Equals(branch.TrimStart('*', ' '), detail.Info.CurrentBranch, StringComparison.Ordinal),
                        isRemote = branch.Contains("remotes/", StringComparison.Ordinal)
                    }),
                    tags = Array.Empty<object>(),
                    recentCommits = detail.RecentCommits.Select(commit => new
                    {
                        hash = string.Empty,
                        shortHash = commit.Split(' ', 2, StringSplitOptions.TrimEntries)[0],
                        message = commit.Contains(' ') ? commit[(commit.IndexOf(' ') + 1)..] : commit,
                        author = string.Empty,
                        authorEmail = string.Empty,
                        date = string.Empty
                    }),
                    remotes = detail.Remotes.Select(remote => new
                    {
                        name = ParseRemoteName(remote),
                        url = ParseRemoteUrl(remote),
                        github = (object?)null
                    }),
                    readmeContent = (string?)null,
                    readmeFilename = (string?)null
                }
            });
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
            return Results.Ok(new
            {
                repositories = repos.Select(r => new
                {
                    path = r.Path,
                    name = r.Name,
                    parentRoot = FindParentRoot(r.Path, allowedRoots)
                }),
                scannedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        })
        .WithName("RefreshRepositories");

        // GET /api/integrations — returns integration statuses
        group.MapGet("/integrations", async (IPluginCatalog pluginCatalog, CancellationToken ct) =>
        {
            var descriptors = await pluginCatalog.GetDescriptorsAsync(ct).ConfigureAwait(false);
            var statuses = await pluginCatalog.GetStatusesAsync(ct).ConfigureAwait(false);

            var statusById = statuses.ToDictionary(status => status.PluginId, StringComparer.Ordinal);

            return Results.Ok(new
            {
                integrations = descriptors.Select(descriptor => new
                {
                    id = descriptor.Id,
                    name = descriptor.DisplayName,
                    status = statusById.TryGetValue(descriptor.Id, out var status)
                        ? status.Status switch
                        {
                            PluginConnectionStatus.Connected => "connected",
                            PluginConnectionStatus.Error => "error",
                            _ => "disconnected",
                        }
                        : "disconnected",
                    connectedAt = statusById.TryGetValue(descriptor.Id, out status)
                        ? status.ConnectedAt
                        : null,
                })
            });
        })
        .WithName("GetIntegrations");

        // GET /api/skills — stub (Phase 7 task 44 will implement)
        group.MapGet("/skills", () => Results.Ok(Array.Empty<object>()))
        .WithName("GetSkills");

        // GET /api/available-tools — stub
        group.MapGet("/available-tools", () => Results.Ok(Array.Empty<object>()))
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
