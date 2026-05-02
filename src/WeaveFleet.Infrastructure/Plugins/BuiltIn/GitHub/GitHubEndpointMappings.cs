using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

internal static class GitHubEndpointMappings
{
    [RequiresUnreferencedCode("Plugin endpoint delegates are registered at runtime via reflection-based ASP.NET Core route building; all parameter types are concrete and preserved at runtime.")]
    public static void MapAuthEndpoints(IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/integrations/github/auth").WithTags("GitHub");

        group.MapPost("/device-code", async (GitHubService gitHubService, CancellationToken ct) =>
        {
            var response = await gitHubService.InitiateDeviceFlowAsync(ct).ConfigureAwait(false);
            if (response is null)
                return Results.Problem("Failed to initiate GitHub device flow.");

            return Results.Ok(new GitHubDeviceCodeApiResponse(
                response.DeviceCode,
                response.UserCode,
                response.VerificationUri,
                response.ExpiresIn,
                response.Interval));
        })
        .WithName("GitHubInitiateDeviceFlow");

        group.MapPost("/poll", async (
            PollRequest request,
            GitHubService gitHubService,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.DeviceCode))
                return Results.BadRequest(new GitHubEndpointError("deviceCode is required."));

            var result = await gitHubService.PollForTokenAsync(userContext.UserId, request.DeviceCode, ct).ConfigureAwait(false);

            return Results.Ok(new GitHubPollApiResponse(
                result.Status switch
                {
                    DeviceFlowPollStatus.Pending => "pending",
                    DeviceFlowPollStatus.Complete => "complete",
                    DeviceFlowPollStatus.Expired => "expired",
                    DeviceFlowPollStatus.Denied => "denied",
                    DeviceFlowPollStatus.Error => "error",
                    _ => throw new InvalidOperationException($"Unsupported device flow poll status '{result.Status}'."),
                },
                result.Interval,
                result.Message));
        })
        .WithName("GitHubPollForToken");

        group.MapPost("/token", async (
            ConnectWithTokenRequest request,
            GitHubService gitHubService,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
                return Results.BadRequest(new GitHubEndpointError("token is required."));

            var connected = await gitHubService.ConnectWithTokenAsync(userContext.UserId, request.Token, ct).ConfigureAwait(false);
            if (!connected)
                return Results.BadRequest(new GitHubEndpointError("Failed to validate GitHub token."));

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
            return Results.Ok(new GitHubConnectionStatusApiResponse(connected));
        })
        .WithName("GitHubConnectionStatus");
    }

    [RequiresUnreferencedCode("Plugin endpoint delegates are registered at runtime via reflection-based ASP.NET Core route building; all parameter types are concrete and preserved at runtime.")]
    public static void MapDataEndpoints(IEndpointRouteBuilder builder)    {
        var group = builder.MapGroup("/api/integrations/github").WithTags("GitHub");

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
                return Results.NotFound(new GitHubEndpointError("PR not found."));

            var sha = pullRequest["head"]?["sha"]?.GetValue<string>();
            if (sha is null)
                return Results.NotFound(new GitHubEndpointError("PR head SHA not found."));

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
                repos.Add(JsonValue.Create<string>(bookmark.FullName));
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
                repos.Add(JsonValue.Create<string>(request.Repo));

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

    public sealed record BookmarkRequest(string Repo);
    public sealed record BookmarkSyncRequest(IReadOnlyList<BookmarkedRepoDto> Bookmarks);
    public sealed record BookmarkedRepoDto(string FullName, string Owner, string Name);

    public sealed record ConnectWithTokenRequest(string Token);

    public sealed record PollRequest(string DeviceCode);
}

// ── Named API response types (file-level so they are accessible from ApiJsonContext in the Api project) ─────

/// <summary>Response from the GitHub device code initiation endpoint.</summary>
public sealed record GitHubDeviceCodeApiResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresIn,
    int Interval);

/// <summary>Response from the GitHub device flow poll endpoint.</summary>
public sealed record GitHubPollApiResponse(string Status, int? Interval, string? Message);

/// <summary>Response from the GitHub connection status endpoint.</summary>
public sealed record GitHubConnectionStatusApiResponse(bool Connected);

/// <summary>Error response body for GitHub plugin endpoints.</summary>
public sealed record GitHubEndpointError(string Error);
