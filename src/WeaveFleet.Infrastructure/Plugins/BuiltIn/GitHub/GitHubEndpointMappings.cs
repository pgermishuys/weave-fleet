using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

internal static class GitHubEndpointMappings
{
    public static void MapAuthEndpoints(WebApplication app, FleetOptions fleetOptions)
    {
        var group = app.MapGroup("/api/integrations/github/auth").WithTags("GitHub");

        if (fleetOptions.Auth.Enabled)
            group.RequireAuthorization("FleetUser");

        group.MapPost("/device-code", async (GitHubService gitHubService, CancellationToken ct) =>
        {
            var response = await gitHubService.InitiateDeviceFlowAsync(ct).ConfigureAwait(false);
            if (response is null)
                return Results.Problem("Failed to initiate GitHub device flow.");

            return Results.Ok(new
            {
                deviceCode = response.DeviceCode,
                userCode = response.UserCode,
                verificationUri = response.VerificationUri,
                expiresIn = response.ExpiresIn,
                interval = response.Interval,
            });
        })
        .WithName("GitHubInitiateDeviceFlow");

        group.MapPost("/poll", async (
            PollRequest request,
            GitHubService gitHubService,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.DeviceCode))
                return Results.BadRequest(new { error = "deviceCode is required." });

            var result = await gitHubService.PollForTokenAsync(userContext.UserId, request.DeviceCode, ct).ConfigureAwait(false);

            return Results.Ok(new
            {
                status = result.Status switch
                {
                    DeviceFlowPollStatus.Pending => "pending",
                    DeviceFlowPollStatus.Complete => "complete",
                    DeviceFlowPollStatus.Expired => "expired",
                    DeviceFlowPollStatus.Denied => "denied",
                    DeviceFlowPollStatus.Error => "error",
                    _ => throw new InvalidOperationException($"Unsupported device flow poll status '{result.Status}'."),
                },
                interval = result.Interval,
                message = result.Message,
            });
        })
        .WithName("GitHubPollForToken");

        group.MapPost("/token", async (
            ConnectWithTokenRequest request,
            GitHubService gitHubService,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
                return Results.BadRequest(new { error = "token is required." });

            var connected = await gitHubService.ConnectWithTokenAsync(userContext.UserId, request.Token, ct).ConfigureAwait(false);
            if (!connected)
                return Results.BadRequest(new { error = "Failed to validate GitHub token." });

            return Results.NoContent();
        })
        .WithName("GitHubConnectWithToken");

        group.MapDelete("/", async (GitHubService gitHubService, IUserContext userContext, CancellationToken ct) =>
        {
            await gitHubService.DisconnectAsync(userContext.UserId, ct).ConfigureAwait(false);
            return Results.NoContent();
        })
        .WithName("GitHubDisconnect");

        group.MapGet("/status", async (GitHubService gitHubService, IUserContext userContext, CancellationToken ct) =>
        {
            var connected = await gitHubService.IsConnectedAsync(userContext.UserId, ct).ConfigureAwait(false);
            return Results.Ok(new { connected });
        })
        .WithName("GitHubConnectionStatus");
    }

    public static void MapDataEndpoints(WebApplication app, FleetOptions fleetOptions)
    {
        var group = app.MapGroup("/api/integrations/github").WithTags("GitHub");

        if (fleetOptions.Auth.Enabled)
            group.RequireAuthorization("FleetUser");

        group.MapGet("/repos", async (
            int? page,
            int? perPage,
            string? sort,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            var query = BuildQuery(
                ("page", page?.ToString(CultureInfo.InvariantCulture)),
                ("per_page", (perPage ?? 100).ToString(CultureInfo.InvariantCulture)),
                ("sort", sort ?? "updated"));
            return await ProxyAsync(gitHubService, proxy, userContext.UserId, $"user/repos{query}", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubListRepos");

        group.MapGet("/repos/{owner}/{repo}/issues", async (
            string owner,
            string repo,
            string? state,
            string? labels,
            int? page,
            int? perPage,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            var pageString = page?.ToString(CultureInfo.InvariantCulture);
            var perPageString = (perPage ?? 30).ToString(CultureInfo.InvariantCulture);
            var query = BuildQuery(("state", state ?? "open"), ("labels", labels), ("page", pageString), ("per_page", perPageString));
            return await ProxyAsync(gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/issues{query}", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubListIssues");

        group.MapGet("/repos/{owner}/{repo}/issues/search", async (
            string owner,
            string repo,
            string? q,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            var query = BuildQuery(("q", q is null ? $"repo:{owner}/{repo}" : $"repo:{owner}/{repo} {q}"), ("type", "issue"));
            return await ProxyAsync(gitHubService, proxy, userContext.UserId, $"search/issues{query}", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubSearchIssues");

        group.MapGet("/repos/{owner}/{repo}/issues/{number:int}", async (
            string owner,
            string repo,
            int number,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            return await ProxyAsync(gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/issues/{number}", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubGetIssue");

        group.MapGet("/repos/{owner}/{repo}/issues/{number:int}/comments", async (
            string owner,
            string repo,
            int number,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            return await ProxyAsync(gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/issues/{number}/comments", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubListIssueComments");

        group.MapGet("/repos/{owner}/{repo}/labels", async (
            string owner,
            string repo,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            return await ProxyAsync(gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/labels?per_page=100", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubListLabels");

        group.MapGet("/repos/{owner}/{repo}/milestones", async (
            string owner,
            string repo,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            return await ProxyAsync(gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/milestones?state=open&per_page=100", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubListMilestones");

        group.MapGet("/repos/{owner}/{repo}/assignees", async (
            string owner,
            string repo,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            return await ProxyAsync(gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/assignees?per_page=100", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubListAssignees");

        group.MapGet("/repos/{owner}/{repo}/pulls", async (
            string owner,
            string repo,
            string? state,
            int? page,
            int? perPage,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            var pageString = page?.ToString(CultureInfo.InvariantCulture);
            var perPageString = (perPage ?? 30).ToString(CultureInfo.InvariantCulture);
            var query = BuildQuery(("state", state ?? "open"), ("page", pageString), ("per_page", perPageString));
            return await ProxyAsync(gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/pulls{query}", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubListPRs");

        group.MapGet("/repos/{owner}/{repo}/pulls/{number:int}", async (
            string owner,
            string repo,
            int number,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            return await ProxyAsync(gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/pulls/{number}", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubGetPR");

        group.MapGet("/repos/{owner}/{repo}/pulls/{number:int}/comments", async (
            string owner,
            string repo,
            int number,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            return await ProxyAsync(gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/pulls/{number}/comments", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubListPRComments");

        group.MapGet("/repos/{owner}/{repo}/pulls/{number:int}/status", async (
            string owner,
            string repo,
            int number,
            GitHubService gitHubService,
            GitHubApiProxy proxy,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            var token = await gitHubService.GetTokenAsync(userContext.UserId, ct).ConfigureAwait(false);
            if (token is null)
                return Results.Unauthorized();

            var pullRequest = await proxy.FetchAsync(token, $"repos/{owner}/{repo}/pulls/{number}", ct: ct).ConfigureAwait(false);
            if (pullRequest is null)
                return Results.NotFound(new { error = "PR not found." });

            var sha = pullRequest["head"]?["sha"]?.GetValue<string>();
            if (sha is null)
                return Results.NotFound(new { error = "PR head SHA not found." });

            var status = await proxy.FetchAsync(token, $"repos/{owner}/{repo}/commits/{sha}/check-runs", ct: ct).ConfigureAwait(false);
            return Results.Ok(status);
        })
        .WithName("GitHubGetPRStatus");

        group.MapGet("/bookmarks", async (IPluginStateStore store, IUserContext userContext, CancellationToken ct) =>
        {
            var config = await store.GetStateAsync("github_bookmarks", userContext.UserId, ct).ConfigureAwait(false);
            var bookmarks = ToBookmarkedRepos(config?["repos"] as JsonArray);
            return Results.Ok(bookmarks);
        })
        .WithName("GitHubGetBookmarks");

        group.MapPut("/bookmarks", async (
            BookmarkSyncRequest request,
            IPluginStateStore store,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            var repos = new JsonArray();
            foreach (var bookmark in request.Bookmarks)
            {
                repos.Add(bookmark.FullName);
            }

            var config = new JsonObject
            {
                ["repos"] = repos,
            };

            await store.SetStateAsync("github_bookmarks", userContext.UserId, config, ct).ConfigureAwait(false);
            return Results.NoContent();
        })
        .WithName("GitHubSyncBookmarks");

        group.MapPost("/bookmarks", async (
            BookmarkRequest request,
            IPluginStateStore store,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            var config = await store.GetStateAsync("github_bookmarks", userContext.UserId, ct).ConfigureAwait(false) ?? [];
            var repos = config["repos"] as JsonArray ?? new JsonArray();
            var exists = repos.Any(repo => repo?.GetValue<string>() == request.Repo);
            if (!exists)
                repos.Add(request.Repo);

            config["repos"] = repos;
            await store.SetStateAsync("github_bookmarks", userContext.UserId, config, ct).ConfigureAwait(false);
            return Results.NoContent();
        })
        .WithName("GitHubAddBookmark");

        group.MapDelete("/bookmarks/{owner}/{repo}", async (
            string owner,
            string repo,
            IPluginStateStore store,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            var fullName = $"{owner}/{repo}";
            var config = await store.GetStateAsync("github_bookmarks", userContext.UserId, ct).ConfigureAwait(false);
            if (config is null)
                return Results.NoContent();

            var repos = config["repos"] as JsonArray;
            if (repos is null)
                return Results.NoContent();

            var item = repos.FirstOrDefault(entry => entry?.GetValue<string>() == fullName);
            if (item is not null)
                repos.Remove(item);

            config["repos"] = repos;
            await store.SetStateAsync("github_bookmarks", userContext.UserId, config, ct).ConfigureAwait(false);
            return Results.NoContent();
        })
        .WithName("GitHubRemoveBookmark");
    }

    private static async Task<IResult> ProxyAsync(
        GitHubService gitHubService,
        GitHubApiProxy proxy,
        string userId,
        string path,
        string method = "GET",
        JsonNode? body = null,
        CancellationToken ct = default)
    {
        var token = await gitHubService.GetTokenAsync(userId, ct).ConfigureAwait(false);
        if (token is null)
            return Results.Unauthorized();

        var result = await proxy.FetchAsync(token, path, method, body, ct).ConfigureAwait(false);
        return result is null
            ? Results.Problem("GitHub API request failed.")
            : Results.Ok(result);
    }

    private static string BuildQuery(params (string Key, string? Value)[] pairs)
    {
        var parts = pairs
            .Where(pair => pair.Value is not null)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}");
        var queryString = string.Join("&", parts);
        return queryString.Length > 0 ? $"?{queryString}" : string.Empty;
    }

    private static BookmarkedRepoDto[] ToBookmarkedRepos(JsonArray? repos)
        => (repos ?? [])
            .Select(entry => entry?.GetValue<string>())
            .Where(fullName => !string.IsNullOrWhiteSpace(fullName))
            .Select(fullName =>
            {
                var parts = fullName!.Split('/', 2, StringSplitOptions.TrimEntries);
                var owner = parts.ElementAtOrDefault(0) ?? string.Empty;
                var name = parts.ElementAtOrDefault(1) ?? string.Empty;
                return new BookmarkedRepoDto(fullName, owner, name);
            })
            .ToArray();

    private sealed record BookmarkRequest(string Repo);
    private sealed record BookmarkSyncRequest(IReadOnlyList<BookmarkedRepoDto> Bookmarks);
    private sealed record BookmarkedRepoDto(string FullName, string Owner, string Name);

    private sealed record ConnectWithTokenRequest(string Token);

    private sealed record PollRequest(string DeviceCode);
}
