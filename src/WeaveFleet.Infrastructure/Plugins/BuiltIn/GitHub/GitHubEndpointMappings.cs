using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

internal static class GitHubEndpointMappings
{
    [RequiresUnreferencedCode("Plugin endpoint delegates are registered at runtime via reflection-based ASP.NET Core route building; all parameter types are concrete and preserved at runtime.")]
    [RequiresDynamicCode("Plugin endpoint delegates are registered at runtime via reflection-based ASP.NET Core route building; all parameter types are concrete and preserved at runtime.")]
    public static void MapAuthEndpoints(IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/integrations/github/auth").WithTags("GitHub");

        group.MapPost("/device-code", async (HttpContext httpContext) =>
        {
            var gitHubService = httpContext.RequestServices.GetRequiredService<GitHubService>();
            var ct = httpContext.RequestAborted;
            var response = await gitHubService.InitiateDeviceFlowAsync(ct).ConfigureAwait(false);
            IResult result = response is null
                ? Results.Problem("Failed to initiate GitHub device flow.")
                : Results.Ok(new GitHubDeviceCodeApiResponse(
                    response.DeviceCode,
                    response.UserCode,
                    response.VerificationUri,
                    response.ExpiresIn,
                    response.Interval));
            await result.ExecuteAsync(httpContext).ConfigureAwait(false);
        })
        .WithName("GitHubInitiateDeviceFlow");

        group.MapPost("/poll", async (HttpContext httpContext) =>
        {
            var gitHubService = httpContext.RequestServices.GetRequiredService<GitHubService>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;
            var request = await httpContext.Request.ReadFromJsonAsync<GitHubPollRequest>(ct).ConfigureAwait(false);

            IResult result;
            if (request is null || string.IsNullOrWhiteSpace(request.DeviceCode))
            {
                result = Results.BadRequest(new GitHubEndpointError("deviceCode is required."));
            }
            else
            {
                var poll = await gitHubService.PollForTokenAsync(userContext.UserId, request.DeviceCode, ct).ConfigureAwait(false);
                result = Results.Ok(new GitHubPollApiResponse(
                    poll.Status switch
                    {
                        DeviceFlowPollStatus.Pending => "pending",
                        DeviceFlowPollStatus.Complete => "complete",
                        DeviceFlowPollStatus.Expired => "expired",
                        DeviceFlowPollStatus.Denied => "denied",
                        DeviceFlowPollStatus.Error => "error",
                        _ => throw new InvalidOperationException($"Unsupported device flow poll status '{poll.Status}'."),
                    },
                    poll.Interval,
                    poll.Message));
            }

            await result.ExecuteAsync(httpContext).ConfigureAwait(false);
        })
        .WithName("GitHubPollForToken");

        group.MapPost("/token", async (HttpContext httpContext) =>
        {
            var gitHubService = httpContext.RequestServices.GetRequiredService<GitHubService>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;
            var request = await httpContext.Request.ReadFromJsonAsync<GitHubConnectWithTokenRequest>(ct).ConfigureAwait(false);

            IResult result;
            if (request is null || string.IsNullOrWhiteSpace(request.Token))
                result = Results.BadRequest(new GitHubEndpointError("token is required."));
            else if (!await gitHubService.ConnectWithTokenAsync(userContext.UserId, request.Token, ct).ConfigureAwait(false))
                result = Results.BadRequest(new GitHubEndpointError("Failed to validate GitHub token."));
            else
                result = Results.NoContent();

            await result.ExecuteAsync(httpContext).ConfigureAwait(false);
        })
        .WithName("GitHubConnectWithToken");

        group.MapDelete("/", async (HttpContext httpContext) =>
        {
            var gitHubService = httpContext.RequestServices.GetRequiredService<GitHubService>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;
            await gitHubService.DisconnectAsync(userContext.UserId, ct).ConfigureAwait(false);
            await Results.NoContent().ExecuteAsync(httpContext).ConfigureAwait(false);
        })
        .WithName("GitHubDisconnect");

        group.MapGet("/status", async (HttpContext httpContext) =>
        {
            var gitHubService = httpContext.RequestServices.GetRequiredService<GitHubService>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;
            var connected = await gitHubService.IsConnectedAsync(userContext.UserId, ct).ConfigureAwait(false);
            await Results.Ok(new GitHubConnectionStatusApiResponse(connected)).ExecuteAsync(httpContext).ConfigureAwait(false);
        })
        .WithName("GitHubConnectionStatus");
    }

    [RequiresUnreferencedCode("Plugin endpoint delegates are registered at runtime via reflection-based ASP.NET Core route building; all parameter types are concrete and preserved at runtime.")]
    [RequiresDynamicCode("Plugin endpoint delegates are registered at runtime via reflection-based ASP.NET Core route building; all parameter types are concrete and preserved at runtime.")]
    public static void MapDataEndpoints(IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/integrations/github").WithTags("GitHub");

        group.MapGet("/repos", async (HttpContext httpContext) =>
        {
            var gitHubService = httpContext.RequestServices.GetRequiredService<GitHubService>();
            var proxy = httpContext.RequestServices.GetRequiredService<GitHubApiProxy>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;

            var page = GetQueryInt(httpContext, "page");
            var perPage = GetQueryInt(httpContext, "perPage") ?? 100;
            var sort = GetQueryString(httpContext, "sort") ?? "updated";

            var query = BuildQuery(
                ("page", page?.ToString(CultureInfo.InvariantCulture)),
                ("per_page", perPage.ToString(CultureInfo.InvariantCulture)),
                ("sort", sort));
            await ProxyAsync(httpContext, gitHubService, proxy, userContext.UserId, $"user/repos{query}", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubListRepos");

        group.MapGet("/repos/{owner}/{repo}/issues", async (HttpContext httpContext) =>
        {
            var gitHubService = httpContext.RequestServices.GetRequiredService<GitHubService>();
            var proxy = httpContext.RequestServices.GetRequiredService<GitHubApiProxy>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;

            var owner = GetRouteString(httpContext, "owner");
            var repo = GetRouteString(httpContext, "repo");
            var state = GetQueryString(httpContext, "state") ?? "open";
            var labels = GetQueryString(httpContext, "labels");
            var page = GetQueryInt(httpContext, "page");
            var perPage = GetQueryInt(httpContext, "perPage") ?? 30;

            var query = BuildQuery(("state", state), ("labels", labels), ("page", page?.ToString(CultureInfo.InvariantCulture)), ("per_page", perPage.ToString(CultureInfo.InvariantCulture)));
            await ProxyAsync(httpContext, gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/issues{query}", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubListIssues");

        group.MapGet("/repos/{owner}/{repo}/issues/search", async (HttpContext httpContext) =>
        {
            var gitHubService = httpContext.RequestServices.GetRequiredService<GitHubService>();
            var proxy = httpContext.RequestServices.GetRequiredService<GitHubApiProxy>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;

            var owner = GetRouteString(httpContext, "owner");
            var repo = GetRouteString(httpContext, "repo");
            var q = GetQueryString(httpContext, "q");

            var query = BuildQuery(("q", q is null ? $"repo:{owner}/{repo}" : $"repo:{owner}/{repo} {q}"), ("type", "issue"));
            await ProxyAsync(httpContext, gitHubService, proxy, userContext.UserId, $"search/issues{query}", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubSearchIssues");

        group.MapGet("/repos/{owner}/{repo}/issues/{number:int}", async (HttpContext httpContext) =>
        {
            var gitHubService = httpContext.RequestServices.GetRequiredService<GitHubService>();
            var proxy = httpContext.RequestServices.GetRequiredService<GitHubApiProxy>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;

            var owner = GetRouteString(httpContext, "owner");
            var repo = GetRouteString(httpContext, "repo");
            var number = GetRouteInt(httpContext, "number");

            await ProxyAsync(httpContext, gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/issues/{number}", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubGetIssue");

        group.MapGet("/repos/{owner}/{repo}/issues/{number:int}/comments", async (HttpContext httpContext) =>
        {
            var gitHubService = httpContext.RequestServices.GetRequiredService<GitHubService>();
            var proxy = httpContext.RequestServices.GetRequiredService<GitHubApiProxy>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;

            var owner = GetRouteString(httpContext, "owner");
            var repo = GetRouteString(httpContext, "repo");
            var number = GetRouteInt(httpContext, "number");

            await ProxyAsync(httpContext, gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/issues/{number}/comments", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubListIssueComments");

        group.MapGet("/repos/{owner}/{repo}/labels", async (HttpContext httpContext) =>
        {
            var gitHubService = httpContext.RequestServices.GetRequiredService<GitHubService>();
            var proxy = httpContext.RequestServices.GetRequiredService<GitHubApiProxy>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;

            var owner = GetRouteString(httpContext, "owner");
            var repo = GetRouteString(httpContext, "repo");

            await ProxyAsync(httpContext, gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/labels?per_page=100", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubListLabels");

        group.MapGet("/repos/{owner}/{repo}/milestones", async (HttpContext httpContext) =>
        {
            var gitHubService = httpContext.RequestServices.GetRequiredService<GitHubService>();
            var proxy = httpContext.RequestServices.GetRequiredService<GitHubApiProxy>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;

            var owner = GetRouteString(httpContext, "owner");
            var repo = GetRouteString(httpContext, "repo");

            await ProxyAsync(httpContext, gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/milestones?state=open&per_page=100", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubListMilestones");

        group.MapGet("/repos/{owner}/{repo}/assignees", async (HttpContext httpContext) =>
        {
            var gitHubService = httpContext.RequestServices.GetRequiredService<GitHubService>();
            var proxy = httpContext.RequestServices.GetRequiredService<GitHubApiProxy>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;

            var owner = GetRouteString(httpContext, "owner");
            var repo = GetRouteString(httpContext, "repo");

            await ProxyAsync(httpContext, gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/assignees?per_page=100", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubListAssignees");

        group.MapGet("/repos/{owner}/{repo}/pulls", async (HttpContext httpContext) =>
        {
            var gitHubService = httpContext.RequestServices.GetRequiredService<GitHubService>();
            var proxy = httpContext.RequestServices.GetRequiredService<GitHubApiProxy>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;

            var owner = GetRouteString(httpContext, "owner");
            var repo = GetRouteString(httpContext, "repo");
            var state = GetQueryString(httpContext, "state") ?? "open";
            var page = GetQueryInt(httpContext, "page");
            var perPage = GetQueryInt(httpContext, "perPage") ?? 30;

            var query = BuildQuery(("state", state), ("page", page?.ToString(CultureInfo.InvariantCulture)), ("per_page", perPage.ToString(CultureInfo.InvariantCulture)));
            await ProxyAsync(httpContext, gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/pulls{query}", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubListPRs");

        group.MapGet("/repos/{owner}/{repo}/pulls/{number:int}", async (HttpContext httpContext) =>
        {
            var gitHubService = httpContext.RequestServices.GetRequiredService<GitHubService>();
            var proxy = httpContext.RequestServices.GetRequiredService<GitHubApiProxy>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;

            var owner = GetRouteString(httpContext, "owner");
            var repo = GetRouteString(httpContext, "repo");
            var number = GetRouteInt(httpContext, "number");

            await ProxyAsync(httpContext, gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/pulls/{number}", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubGetPR");

        group.MapGet("/repos/{owner}/{repo}/pulls/{number:int}/comments", async (HttpContext httpContext) =>
        {
            var gitHubService = httpContext.RequestServices.GetRequiredService<GitHubService>();
            var proxy = httpContext.RequestServices.GetRequiredService<GitHubApiProxy>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;

            var owner = GetRouteString(httpContext, "owner");
            var repo = GetRouteString(httpContext, "repo");
            var number = GetRouteInt(httpContext, "number");

            await ProxyAsync(httpContext, gitHubService, proxy, userContext.UserId, $"repos/{owner}/{repo}/pulls/{number}/comments", ct: ct).ConfigureAwait(false);
        })
        .WithName("GitHubListPRComments");

        group.MapGet("/repos/{owner}/{repo}/pulls/{number:int}/status", async (HttpContext httpContext) =>
        {
            var gitHubService = httpContext.RequestServices.GetRequiredService<GitHubService>();
            var proxy = httpContext.RequestServices.GetRequiredService<GitHubApiProxy>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;

            var owner = GetRouteString(httpContext, "owner");
            var repo = GetRouteString(httpContext, "repo");
            var number = GetRouteInt(httpContext, "number");

            var token = await gitHubService.GetTokenAsync(userContext.UserId, ct).ConfigureAwait(false);
            if (token is null)
            {
                await Results.Unauthorized().ExecuteAsync(httpContext).ConfigureAwait(false);
                return;
            }

            var pullRequest = await proxy.FetchAsync(token, $"repos/{owner}/{repo}/pulls/{number}", ct: ct).ConfigureAwait(false);
            if (pullRequest is null)
            {
                await Results.NotFound(new GitHubEndpointError("PR not found.")).ExecuteAsync(httpContext).ConfigureAwait(false);
                return;
            }

            var sha = pullRequest["head"]?["sha"]?.GetValue<string>();
            if (sha is null)
            {
                await Results.NotFound(new GitHubEndpointError("PR head SHA not found.")).ExecuteAsync(httpContext).ConfigureAwait(false);
                return;
            }

            var status = await proxy.FetchAsync(token, $"repos/{owner}/{repo}/commits/{sha}/check-runs", ct: ct).ConfigureAwait(false);
            await Results.Ok(status).ExecuteAsync(httpContext).ConfigureAwait(false);
        })
        .WithName("GitHubGetPRStatus");

        group.MapGet("/bookmarks", async (HttpContext httpContext) =>
        {
            var store = httpContext.RequestServices.GetRequiredService<IPluginStateStore>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;

            var config = await store.GetStateAsync("github_bookmarks", userContext.UserId, ct).ConfigureAwait(false);
            var bookmarks = ToBookmarkedRepos(config?["repos"] as JsonArray);
            await Results.Ok(bookmarks).ExecuteAsync(httpContext).ConfigureAwait(false);
        })
        .WithName("GitHubGetBookmarks");

        group.MapPut("/bookmarks", async (HttpContext httpContext) =>
        {
            var store = httpContext.RequestServices.GetRequiredService<IPluginStateStore>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;
            var request = await httpContext.Request.ReadFromJsonAsync<GitHubBookmarkSyncRequest>(ct).ConfigureAwait(false);

            if (request is null)
            {
                await Results.BadRequest(new GitHubEndpointError("Invalid request body.")).ExecuteAsync(httpContext).ConfigureAwait(false);
                return;
            }

            var repos = new JsonArray();
            foreach (var bookmark in request.Bookmarks)
            {
                repos.Add((JsonNode)bookmark.FullName);
            }

            var config = new JsonObject
            {
                ["repos"] = repos,
            };

            await store.SetStateAsync("github_bookmarks", userContext.UserId, config, ct).ConfigureAwait(false);
            await Results.NoContent().ExecuteAsync(httpContext).ConfigureAwait(false);
        })
        .WithName("GitHubSyncBookmarks");

        group.MapPost("/bookmarks", async (HttpContext httpContext) =>
        {
            var store = httpContext.RequestServices.GetRequiredService<IPluginStateStore>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;
            var request = await httpContext.Request.ReadFromJsonAsync<GitHubBookmarkRequest>(ct).ConfigureAwait(false);

            if (request is null)
            {
                await Results.BadRequest(new GitHubEndpointError("Invalid request body.")).ExecuteAsync(httpContext).ConfigureAwait(false);
                return;
            }

            var config = await store.GetStateAsync("github_bookmarks", userContext.UserId, ct).ConfigureAwait(false) ?? [];
            var repos = config["repos"] as JsonArray ?? new JsonArray();
            var exists = repos.Any(repo => repo?.GetValue<string>() == request.Repo);
            if (!exists)
                repos.Add((JsonNode)request.Repo);

            config["repos"] = repos;
            await store.SetStateAsync("github_bookmarks", userContext.UserId, config, ct).ConfigureAwait(false);
            await Results.NoContent().ExecuteAsync(httpContext).ConfigureAwait(false);
        })
        .WithName("GitHubAddBookmark");

        group.MapDelete("/bookmarks/{owner}/{repo}", async (HttpContext httpContext) =>
        {
            var store = httpContext.RequestServices.GetRequiredService<IPluginStateStore>();
            var userContext = httpContext.RequestServices.GetRequiredService<IUserContext>();
            var ct = httpContext.RequestAborted;

            var owner = GetRouteString(httpContext, "owner");
            var repo = GetRouteString(httpContext, "repo");
            var fullName = $"{owner}/{repo}";

            var config = await store.GetStateAsync("github_bookmarks", userContext.UserId, ct).ConfigureAwait(false);
            if (config is not null)
            {
                var repos = config["repos"] as JsonArray;
                if (repos is not null)
                {
                    var item = repos.FirstOrDefault(entry => entry?.GetValue<string>() == fullName);
                    if (item is not null)
                        repos.Remove(item);

                    config["repos"] = repos;
                    await store.SetStateAsync("github_bookmarks", userContext.UserId, config, ct).ConfigureAwait(false);
                }
            }

            await Results.NoContent().ExecuteAsync(httpContext).ConfigureAwait(false);
        })
        .WithName("GitHubRemoveBookmark");
    }

    private static string GetRouteString(HttpContext httpContext, string key)
        => httpContext.Request.RouteValues[key]?.ToString() ?? string.Empty;

    private static int GetRouteInt(HttpContext httpContext, string key)
        => int.TryParse(httpContext.Request.RouteValues[key]?.ToString(), CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static string? GetQueryString(HttpContext httpContext, string key)
        => httpContext.Request.Query.TryGetValue(key, out var values) ? (string?)values : null;

    private static int? GetQueryInt(HttpContext httpContext, string key)
        => httpContext.Request.Query.TryGetValue(key, out var values) && int.TryParse((string?)values, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static async Task ProxyAsync(
        HttpContext httpContext,
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
        {
            await Results.Unauthorized().ExecuteAsync(httpContext).ConfigureAwait(false);
            return;
        }

        var result = await proxy.FetchAsync(token, path, method, body, ct).ConfigureAwait(false);
        IResult response = result is null
            ? Results.Problem("GitHub API request failed.")
            : Results.Ok(result);
        await response.ExecuteAsync(httpContext).ConfigureAwait(false);
    }

    private static string BuildQuery(params (string Key, string? Value)[] pairs)
    {
        var parts = pairs
            .Where(pair => pair.Value is not null)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}");
        var queryString = string.Join("&", parts);
        return queryString.Length > 0 ? $"?{queryString}" : string.Empty;
    }

    private static GitHubBookmarkedRepoDto[] ToBookmarkedRepos(JsonArray? repos)
        => (repos ?? [])
            .Select(entry => entry?.GetValue<string>())
            .Where(fullName => !string.IsNullOrWhiteSpace(fullName))
            .Select(fullName =>
            {
                var parts = fullName!.Split('/', 2, StringSplitOptions.TrimEntries);
                var owner = parts.ElementAtOrDefault(0) ?? string.Empty;
                var name = parts.ElementAtOrDefault(1) ?? string.Empty;
                return new GitHubBookmarkedRepoDto(fullName, owner, name);
            })
            .ToArray();
}

// ── Named API request types (file-level so they are accessible from ApiJsonContext in the Api project) ─────

public sealed record GitHubBookmarkRequest(string Repo);
public sealed record GitHubBookmarkSyncRequest(IReadOnlyList<GitHubBookmarkedRepoDto> Bookmarks);
public sealed record GitHubBookmarkedRepoDto(string FullName, string Owner, string Name);
public sealed record GitHubConnectWithTokenRequest(string Token);
public sealed record GitHubPollRequest(string DeviceCode);

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
