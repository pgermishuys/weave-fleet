using System.Globalization;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Api.Endpoints;

/// <summary>
/// GitHub REST API proxy endpoints. All requests are authenticated with the stored GitHub token.
/// </summary>
public static class GitHubEndpoints
{
    public static WebApplication MapGitHubEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/integrations/github").WithTags("GitHub");

        // GET /api/integrations/github/repos — list authenticated user repos
        group.MapGet("/repos", async (
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            CancellationToken ct) =>
        {
            return await ProxyAsync(gitHubService, proxy, "user/repos?per_page=100&sort=updated", ct: ct);
        })
        .WithName("GitHubListRepos");

        // GET /api/integrations/github/repos/{owner}/{repo}/issues
        group.MapGet("/repos/{owner}/{repo}/issues", async (
            string owner,
            string repo,
            string? state,
            string? labels,
            int? page,
            int? perPage,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            CancellationToken ct) =>
        {
            var pageStr = page?.ToString(CultureInfo.InvariantCulture);
            var perPageStr = (perPage ?? 30).ToString(CultureInfo.InvariantCulture);
            var q = BuildQuery(("state", state ?? "open"), ("labels", labels), ("page", pageStr), ("per_page", perPageStr));
            return await ProxyAsync(gitHubService, proxy, $"repos/{owner}/{repo}/issues{q}", ct: ct);
        })
        .WithName("GitHubListIssues");

        // GET /api/integrations/github/repos/{owner}/{repo}/issues/search
        group.MapGet("/repos/{owner}/{repo}/issues/search", async (
            string owner,
            string repo,
            string? q,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            CancellationToken ct) =>
        {
            var query = BuildQuery(("q", q is null ? $"repo:{owner}/{repo}" : $"repo:{owner}/{repo} {q}"), ("type", "issue"));
            return await ProxyAsync(gitHubService, proxy, $"search/issues{query}", ct: ct);
        })
        .WithName("GitHubSearchIssues");

        // GET /api/integrations/github/repos/{owner}/{repo}/issues/{number}
        group.MapGet("/repos/{owner}/{repo}/issues/{number:int}", async (
            string owner,
            string repo,
            int number,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            CancellationToken ct) =>
        {
            return await ProxyAsync(gitHubService, proxy, $"repos/{owner}/{repo}/issues/{number}", ct: ct);
        })
        .WithName("GitHubGetIssue");

        // GET /api/integrations/github/repos/{owner}/{repo}/issues/{number}/comments
        group.MapGet("/repos/{owner}/{repo}/issues/{number:int}/comments", async (
            string owner,
            string repo,
            int number,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            CancellationToken ct) =>
        {
            return await ProxyAsync(gitHubService, proxy, $"repos/{owner}/{repo}/issues/{number}/comments", ct: ct);
        })
        .WithName("GitHubListIssueComments");

        // GET /api/integrations/github/repos/{owner}/{repo}/labels
        group.MapGet("/repos/{owner}/{repo}/labels", async (
            string owner,
            string repo,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            CancellationToken ct) =>
        {
            return await ProxyAsync(gitHubService, proxy, $"repos/{owner}/{repo}/labels?per_page=100", ct: ct);
        })
        .WithName("GitHubListLabels");

        // GET /api/integrations/github/repos/{owner}/{repo}/milestones
        group.MapGet("/repos/{owner}/{repo}/milestones", async (
            string owner,
            string repo,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            CancellationToken ct) =>
        {
            return await ProxyAsync(gitHubService, proxy, $"repos/{owner}/{repo}/milestones?state=open&per_page=100", ct: ct);
        })
        .WithName("GitHubListMilestones");

        // GET /api/integrations/github/repos/{owner}/{repo}/assignees
        group.MapGet("/repos/{owner}/{repo}/assignees", async (
            string owner,
            string repo,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            CancellationToken ct) =>
        {
            return await ProxyAsync(gitHubService, proxy, $"repos/{owner}/{repo}/assignees?per_page=100", ct: ct);
        })
        .WithName("GitHubListAssignees");

        // GET /api/integrations/github/repos/{owner}/{repo}/pulls
        group.MapGet("/repos/{owner}/{repo}/pulls", async (
            string owner,
            string repo,
            string? state,
            int? page,
            int? perPage,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            CancellationToken ct) =>
        {
            var pageStr = page?.ToString(CultureInfo.InvariantCulture);
            var perPageStr = (perPage ?? 30).ToString(CultureInfo.InvariantCulture);
            var q = BuildQuery(("state", state ?? "open"), ("page", pageStr), ("per_page", perPageStr));
            return await ProxyAsync(gitHubService, proxy, $"repos/{owner}/{repo}/pulls{q}", ct: ct);
        })
        .WithName("GitHubListPRs");

        // GET /api/integrations/github/repos/{owner}/{repo}/pulls/{number}
        group.MapGet("/repos/{owner}/{repo}/pulls/{number:int}", async (
            string owner,
            string repo,
            int number,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            CancellationToken ct) =>
        {
            return await ProxyAsync(gitHubService, proxy, $"repos/{owner}/{repo}/pulls/{number}", ct: ct);
        })
        .WithName("GitHubGetPR");

        // GET /api/integrations/github/repos/{owner}/{repo}/pulls/{number}/comments
        group.MapGet("/repos/{owner}/{repo}/pulls/{number:int}/comments", async (
            string owner,
            string repo,
            int number,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            CancellationToken ct) =>
        {
            return await ProxyAsync(gitHubService, proxy, $"repos/{owner}/{repo}/pulls/{number}/comments", ct: ct);
        })
        .WithName("GitHubListPRComments");

        // GET /api/integrations/github/repos/{owner}/{repo}/pulls/{number}/status
        group.MapGet("/repos/{owner}/{repo}/pulls/{number:int}/status", async (
            string owner,
            string repo,
            int number,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            CancellationToken ct) =>
        {
            var token = await gitHubService.GetTokenAsync(ct);
            if (token is null)
                return Results.Unauthorized();

            // Fetch the PR to get the head SHA, then fetch check runs
            var pr = await proxy.FetchAsync(token, $"repos/{owner}/{repo}/pulls/{number}", ct: ct);
            if (pr is null)
                return Results.NotFound(new { error = "PR not found." });

            var sha = pr["head"]?["sha"]?.GetValue<string>();
            if (sha is null)
                return Results.NotFound(new { error = "PR head SHA not found." });

            var status = await proxy.FetchAsync(token, $"repos/{owner}/{repo}/commits/{sha}/check-runs", ct: ct);
            return Results.Ok(status);
        })
        .WithName("GitHubGetPRStatus");

        // GET /api/integrations/github/bookmarks — stored bookmarked repos
        group.MapGet("/bookmarks", async (IIntegrationStore store, CancellationToken ct) =>
        {
            var config = await store.GetConfigAsync("github_bookmarks", ct);
            var bookmarks = config?["repos"] as JsonArray ?? [];
            return Results.Ok(new { bookmarks });
        })
        .WithName("GitHubGetBookmarks");

        // POST /api/integrations/github/bookmarks — add a bookmark
        group.MapPost("/bookmarks", async (
            BookmarkRequest req,
            IIntegrationStore store,
            CancellationToken ct) =>
        {
            var config = await store.GetConfigAsync("github_bookmarks", ct) ?? [];
            var repos = config["repos"] as JsonArray ?? new JsonArray();
            var exists = repos.Any(r => r?.GetValue<string>() == req.Repo);
            if (!exists)
                repos.Add(req.Repo);
            config["repos"] = repos;
            await store.SetConfigAsync("github_bookmarks", config, ct);
            return Results.NoContent();
        })
        .WithName("GitHubAddBookmark");

        // DELETE /api/integrations/github/bookmarks/{owner}/{repo}
        group.MapDelete("/bookmarks/{owner}/{repo}", async (
            string owner,
            string repo,
            IIntegrationStore store,
            CancellationToken ct) =>
        {
            var fullName = $"{owner}/{repo}";
            var config = await store.GetConfigAsync("github_bookmarks", ct);
            if (config is null) return Results.NoContent();

            var repos = config["repos"] as JsonArray;
            if (repos is null) return Results.NoContent();

            var item = repos.FirstOrDefault(r => r?.GetValue<string>() == fullName);
            if (item is not null) repos.Remove(item);

            config["repos"] = repos;
            await store.SetConfigAsync("github_bookmarks", config, ct);
            return Results.NoContent();
        })
        .WithName("GitHubRemoveBookmark");

        return app;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<IResult> ProxyAsync(
        GitHubService gitHubService,
        GitHubApiProxy proxy,
        string path,
        string method = "GET",
        JsonNode? body = null,
        CancellationToken ct = default)
    {
        var token = await gitHubService.GetTokenAsync(ct);
        if (token is null)
            return Results.Unauthorized();

        var result = await proxy.FetchAsync(token, path, method, body, ct);
        if (result is null)
            return Results.Problem("GitHub API request failed.");

        return Results.Ok(result);
    }

    private static string BuildQuery(params (string Key, string? Value)[] pairs)
    {
        var parts = pairs
            .Where(p => p.Value is not null)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}");
        var qs = string.Join("&", parts);
        return qs.Length > 0 ? $"?{qs}" : string.Empty;
    }

    private sealed record BookmarkRequest(string Repo);
}
