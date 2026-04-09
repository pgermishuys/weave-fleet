using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Api.Endpoints;

public static class FleetEndpoints
{
    public static WebApplication MapFleetSummaryEndpoints(this WebApplication app)
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

        // GET /api/version — assembly version + service version from telemetry
        group.MapGet("/version", () => Results.Ok(new
        {
            version = FleetInstrumentation.ServiceVersion,
            commit = "dev"
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
            CancellationToken ct) =>
        {
            var repos = await repoService.ScanRepositoriesAsync(ct);
            return Results.Ok(new
            {
                repositories = repos.Select(r => new
                {
                    path = r.Path,
                    name = r.Name,
                    currentBranch = r.CurrentBranch,
                    remoteUrl = r.RemoteUrl,
                    lastCommitMessage = r.LastCommitMessage
                }),
                scannedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        })
        .WithName("GetRepositories");

        // GET /api/repositories/info?path= — single repo metadata
        group.MapGet("/repositories/info", async (
            string path,
            RepositoryService repoService,
            WorkspaceRootService workspaceRootService,
            CancellationToken ct) =>
        {
            var normalised = Path.GetFullPath(path);
            var allowedRoots = await workspaceRootService.GetAllowedRootsAsync();
            if (!OpenDirectoryEndpoints.IsUnderAllowedRoot(normalised, allowedRoots))
                return Results.BadRequest(new { error = "Path is outside allowed workspace roots." });

            var info = await repoService.GetRepositoryInfoAsync(normalised, ct);
            if (info is null)
                return Results.NotFound(new { error = "Not a git repository." });

            return Results.Ok(new
            {
                path = info.Path,
                name = info.Name,
                currentBranch = info.CurrentBranch,
                remoteUrl = info.RemoteUrl,
                lastCommitMessage = info.LastCommitMessage
            });
        })
        .WithName("GetRepositoryInfo");

        // GET /api/repositories/detail?path= — enriched repo detail
        group.MapGet("/repositories/detail", async (
            string path,
            RepositoryService repoService,
            WorkspaceRootService workspaceRootService,
            CancellationToken ct) =>
        {
            var normalised = Path.GetFullPath(path);
            var allowedRoots = await workspaceRootService.GetAllowedRootsAsync();
            if (!OpenDirectoryEndpoints.IsUnderAllowedRoot(normalised, allowedRoots))
                return Results.BadRequest(new { error = "Path is outside allowed workspace roots." });

            var detail = await repoService.GetRepositoryDetailAsync(normalised, ct);
            if (detail is null)
                return Results.NotFound(new { error = "Not a git repository." });

            return Results.Ok(new
            {
                path = detail.Info.Path,
                name = detail.Info.Name,
                currentBranch = detail.Info.CurrentBranch,
                remoteUrl = detail.Info.RemoteUrl,
                lastCommitMessage = detail.Info.LastCommitMessage,
                branches = detail.Branches,
                remotes = detail.Remotes,
                recentCommits = detail.RecentCommits
            });
        })
        .WithName("GetRepositoryDetail");

        // POST /api/repositories/refresh — invalidate cache
        group.MapPost("/repositories/refresh", async (
            RepositoryService repoService,
            CancellationToken ct) =>
        {
            await repoService.RefreshScanAsync(ct);
            return Results.NoContent();
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
}
