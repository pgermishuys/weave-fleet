using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

/// <summary>
/// Pull requests and issues as Fleet shows them in lists and on their pages, read from GitHub's GraphQL API:
/// one request carries a pull request's checks, review decision and reviewers, which the REST API spreads
/// over several calls per pull request.
/// </summary>
internal static class GitHubItems
{
    private const string PullRequestFields = """
        fragment Pr on PullRequest {
          __typename number title url state isDraft createdAt updatedAt
          author { login avatarUrl }
          repository { name owner { login } }
          comments { totalCount }
          labels(first: 10) { nodes { name color } }
          headRefName baseRefName additions deletions changedFiles mergeable reviewDecision
          latestOpinionatedReviews(first: 10) { nodes { state author { login avatarUrl } } }
          reviewRequests(first: 10) { nodes { requestedReviewer { __typename ... on User { login avatarUrl } ... on Team { name } } } }
          commits(last: 1) { nodes { commit { statusCheckRollup { state } } } }
        }
        """;

    private const string IssueFields = """
        fragment Iss on Issue {
          __typename number title url state createdAt updatedAt
          author { login avatarUrl }
          repository { name owner { login } }
          comments { totalCount }
          labels(first: 10) { nodes { name color } }
          assignees(first: 5) { nodes { login } }
        }
        """;

    internal const string SearchQuery = PullRequestFields + IssueFields + """
        query($q: String!, $first: Int!, $after: String) {
          search(query: $q, type: ISSUE, first: $first, after: $after) {
            issueCount
            pageInfo { endCursor hasNextPage }
            nodes { ...Pr ...Iss }
          }
        }
        """;

    internal const string WorkQuery = PullRequestFields + IssueFields + """
        query($review: String!, $authored: String!, $assigned: String!) {
          viewer { login }
          review: search(query: $review, type: ISSUE, first: 20) { nodes { ...Pr } }
          authored: search(query: $authored, type: ISSUE, first: 20) { nodes { ...Pr } }
          assigned: search(query: $assigned, type: ISSUE, first: 20) { nodes { ...Iss ...Pr } }
        }
        """;

    internal const string DetailQuery = PullRequestFields + IssueFields + """
        query($owner: String!, $repo: String!, $number: Int!) {
          repository(owner: $owner, name: $repo) {
            issueOrPullRequest(number: $number) {
              ...Pr
              ...Iss
              ... on PullRequest {
                body mergedAt
                mergedBy { login }
                reviewThreads(first: 100) { nodes { isResolved isOutdated } }
                commits(last: 1) {
                  nodes {
                    commit {
                      statusCheckRollup {
                        contexts(first: 100) {
                          nodes {
                            __typename
                            ... on CheckRun {
                              name status conclusion detailsUrl startedAt completedAt databaseId
                              checkSuite { workflowRun { workflow { name } } }
                            }
                            ... on StatusContext { context state targetUrl createdAt }
                          }
                        }
                      }
                    }
                  }
                }
                timelineItems(last: 60, itemTypes: [ISSUE_COMMENT, PULL_REQUEST_REVIEW, MERGED_EVENT, CLOSED_EVENT, REOPENED_EVENT, READY_FOR_REVIEW_EVENT, CONVERT_TO_DRAFT_EVENT, CROSS_REFERENCED_EVENT]) {
                  nodes { ...Timeline }
                }
              }
              ... on Issue {
                body
                timelineItems(last: 60, itemTypes: [ISSUE_COMMENT, CLOSED_EVENT, REOPENED_EVENT, CROSS_REFERENCED_EVENT]) {
                  nodes { ...Timeline }
                }
              }
            }
          }
        }

        fragment Timeline on Node {
          __typename
          ... on IssueComment { author { login avatarUrl } createdAt body url }
          ... on PullRequestReview { author { login avatarUrl } submittedAt createdAt body state url }
          ... on MergedEvent { actor { login avatarUrl } createdAt }
          ... on ClosedEvent { actor { login avatarUrl } createdAt }
          ... on ReopenedEvent { actor { login avatarUrl } createdAt }
          ... on ReadyForReviewEvent { actor { login avatarUrl } createdAt }
          ... on ConvertToDraftEvent { actor { login avatarUrl } createdAt }
          ... on CrossReferencedEvent {
            actor { login avatarUrl } createdAt
            source {
              __typename
              ... on PullRequest { number title url state repository { nameWithOwner } }
              ... on Issue { number title url state repository { nameWithOwner } }
            }
          }
        }
        """;

    /// <summary>
    /// A query for items picked by number. GraphQL has no "these numbers" filter, so each number gets its own
    /// aliased field (<c>i0</c>, <c>i1</c>…) in one request.
    /// </summary>
    internal static (string Query, JsonObject Variables) BuildNumbersQuery(string owner, string repo, IReadOnlyList<int> numbers)
    {
        var fields = new StringBuilder();
        for (var i = 0; i < numbers.Count; i++)
        {
            fields.Append(CultureInfo.InvariantCulture, $"i{i}: issueOrPullRequest(number: {numbers[i]}) {{ ...Pr ...Iss }}\n");
        }

        var query = PullRequestFields + IssueFields
            + "query($owner: String!, $repo: String!) {\n  repository(owner: $owner, name: $repo) {\n"
            + fields
            + "  }\n}\n";
        return (query, new JsonObject { ["owner"] = owner, ["repo"] = repo });
    }

    /// <summary>Open pull request and issue counts for each repository, one aliased field per repository.</summary>
    internal static (string Query, JsonObject Variables) BuildRepoCountsQuery(IReadOnlyList<(string Owner, string Name)> repos)
    {
        var parameters = new StringBuilder();
        var fields = new StringBuilder();
        var variables = new JsonObject();
        for (var i = 0; i < repos.Count; i++)
        {
            parameters.Append(CultureInfo.InvariantCulture, $"$o{i}: String!, $n{i}: String!, ");
            fields.Append(CultureInfo.InvariantCulture,
                $"r{i}: repository(owner: $o{i}, name: $n{i}) {{ nameWithOwner pullRequests(states: OPEN) {{ totalCount }} issues(states: OPEN) {{ totalCount }} }}\n");
            variables[$"o{i}"] = repos[i].Owner;
            variables[$"n{i}"] = repos[i].Name;
        }

        return ($"query({parameters.ToString().TrimEnd(' ', ',')}) {{\n{fields}}}\n", variables);
    }

    /// <summary>The message from GitHub's first GraphQL error, when the response carries one.</summary>
    internal static string? ErrorMessage(JsonNode? response)
        => (response?["errors"] as JsonArray)?.OfType<JsonObject>().Select(e => e["message"]?.GetValue<string>()).FirstOrDefault(m => m is not null);

    internal static GitHubItemPage ParseSearch(JsonNode? response)
    {
        var search = response?["data"]?["search"];
        return new GitHubItemPage(
            ParseNodes(search?["nodes"] as JsonArray),
            search?["issueCount"]?.GetValue<int>() ?? 0,
            search?["pageInfo"]?["endCursor"]?.GetValue<string>(),
            search?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() ?? false);
    }

    internal static GitHubWorkResponse ParseWork(JsonNode? response, IReadOnlyList<GitHubRepoCounts> repos)
    {
        var data = response?["data"];
        return new GitHubWorkResponse(
            data?["viewer"]?["login"]?.GetValue<string>(),
            ParseNodes(data?["review"]?["nodes"] as JsonArray),
            ParseNodes(data?["authored"]?["nodes"] as JsonArray),
            ParseNodes(data?["assigned"]?["nodes"] as JsonArray),
            repos);
    }

    internal static IReadOnlyList<GitHubRepoCounts> ParseRepoCounts(JsonNode? response)
    {
        if (response?["data"] is not JsonObject data)
            return [];

        return data
            .Select(pair => pair.Value)
            .OfType<JsonObject>()
            .Select(repo => new GitHubRepoCounts(
                repo["nameWithOwner"]?.GetValue<string>() ?? string.Empty,
                repo["pullRequests"]?["totalCount"]?.GetValue<int>() ?? 0,
                repo["issues"]?["totalCount"]?.GetValue<int>() ?? 0))
            .Where(repo => repo.FullName.Length > 0)
            .ToArray();
    }

    /// <summary>Items picked by number, in the order asked for; numbers GitHub doesn't know are left out.</summary>
    internal static IReadOnlyList<GitHubItemSummary> ParseNumbers(JsonNode? response, int count)
    {
        var repository = response?["data"]?["repository"];
        var items = new List<GitHubItemSummary>();
        for (var i = 0; i < count; i++)
        {
            if (repository?[$"i{i}"] is JsonObject node && ParseSummary(node) is { } item)
                items.Add(item);
        }

        return items;
    }

    internal static GitHubItemDetail? ParseDetail(JsonNode? response)
    {
        if (response?["data"]?["repository"]?["issueOrPullRequest"] is not JsonObject node || ParseSummary(node) is not { } summary)
            return null;

        var threads = node["reviewThreads"]?["nodes"] as JsonArray ?? [];
        var contexts = node["commits"]?["nodes"]?[0]?["commit"]?["statusCheckRollup"]?["contexts"]?["nodes"] as JsonArray ?? [];

        return new GitHubItemDetail(
            summary,
            node["body"]?.GetValue<string>(),
            node["changedFiles"]?.GetValue<int>(),
            node["mergedAt"]?.GetValue<string>(),
            node["mergedBy"]?["login"]?.GetValue<string>(),
            contexts.OfType<JsonObject>().Select(ParseCheck).OfType<GitHubCheckDto>().ToArray(),
            threads.OfType<JsonObject>().Count(t => t["isResolved"]?.GetValue<bool>() != true && t["isOutdated"]?.GetValue<bool>() != true),
            (node["timelineItems"]?["nodes"] as JsonArray ?? []).OfType<JsonObject>().Select(ParseTimelineEntry).OfType<GitHubTimelineEntry>().ToArray());
    }

    private static GitHubItemSummary[] ParseNodes(JsonArray? nodes)
        => (nodes ?? []).OfType<JsonObject>().Select(ParseSummary).OfType<GitHubItemSummary>().ToArray();

    internal static GitHubItemSummary? ParseSummary(JsonObject node)
    {
        var typeName = node["__typename"]?.GetValue<string>();
        if (typeName is not ("PullRequest" or "Issue"))
            return null;

        var isPull = typeName == "PullRequest";
        var labels = (node["labels"]?["nodes"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(l => new GitHubLabelDto(l["name"]?.GetValue<string>() ?? string.Empty, l["color"]?.GetValue<string>() ?? string.Empty))
            .ToArray();

        return new GitHubItemSummary(
            isPull ? "pull" : "issue",
            node["repository"]?["owner"]?["login"]?.GetValue<string>() ?? string.Empty,
            node["repository"]?["name"]?.GetValue<string>() ?? string.Empty,
            node["number"]?.GetValue<int>() ?? 0,
            node["title"]?.GetValue<string>() ?? string.Empty,
            node["url"]?.GetValue<string>() ?? string.Empty,
            (node["state"]?.GetValue<string>() ?? "OPEN").ToLowerInvariant(),
            node["isDraft"]?.GetValue<bool>() == true,
            node["author"]?["login"]?.GetValue<string>(),
            node["author"]?["avatarUrl"]?.GetValue<string>(),
            node["createdAt"]?.GetValue<string>() ?? string.Empty,
            node["updatedAt"]?.GetValue<string>() ?? string.Empty,
            node["comments"]?["totalCount"]?.GetValue<int>() ?? 0,
            labels,
            isPull ? node["headRefName"]?.GetValue<string>() : null,
            isPull ? node["baseRefName"]?.GetValue<string>() : null,
            isPull ? node["additions"]?.GetValue<int>() : null,
            isPull ? node["deletions"]?.GetValue<int>() : null,
            isPull ? ChecksRollup(node["commits"]?["nodes"]?[0]?["commit"]?["statusCheckRollup"]?["state"]?.GetValue<string>()) : null,
            isPull ? node["reviewDecision"]?.GetValue<string>() : null,
            isPull ? node["mergeable"]?.GetValue<string>() : null,
            isPull ? ParseReviewers(node) : [],
            (node["assignees"]?["nodes"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(a => a["login"]?.GetValue<string>()).OfType<string>().ToArray());
    }

    /// <summary>"success", "failure", "pending" or "none", from GitHub's rollup of a commit's checks.</summary>
    internal static string ChecksRollup(string? rollupState) => rollupState switch
    {
        "SUCCESS" => "success",
        "FAILURE" or "ERROR" => "failure",
        "PENDING" or "EXPECTED" => "pending",
        _ => "none",
    };

    /// <summary>
    /// Everyone with a say on a pull request: each reviewer's latest verdict, then anyone asked who hasn't
    /// answered yet (state <c>REQUESTED</c>).
    /// </summary>
    internal static GitHubReviewerDto[] ParseReviewers(JsonNode pullRequest)
    {
        var reviewers = new List<GitHubReviewerDto>();
        foreach (var review in (pullRequest["latestOpinionatedReviews"]?["nodes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var login = review["author"]?["login"]?.GetValue<string>();
            if (login is null || reviewers.Any(r => r.Login == login))
                continue;
            reviewers.Add(new GitHubReviewerDto(login, review["author"]?["avatarUrl"]?.GetValue<string>(), review["state"]?.GetValue<string>() ?? "COMMENTED"));
        }

        foreach (var request in (pullRequest["reviewRequests"]?["nodes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var reviewer = request["requestedReviewer"];
            var login = reviewer?["login"]?.GetValue<string>() ?? reviewer?["name"]?.GetValue<string>();
            if (login is null || reviewers.Any(r => r.Login == login))
                continue;
            reviewers.Add(new GitHubReviewerDto(login, reviewer?["avatarUrl"]?.GetValue<string>(), "REQUESTED"));
        }

        return reviewers.ToArray();
    }

    private static GitHubCheckDto? ParseCheck(JsonObject context)
    {
        if (context["__typename"]?.GetValue<string>() == "StatusContext")
        {
            var state = context["state"]?.GetValue<string>();
            return new GitHubCheckDto(
                context["context"]?.GetValue<string>() ?? string.Empty,
                state switch { "SUCCESS" => "success", "FAILURE" or "ERROR" => "failure", _ => "pending" },
                context["targetUrl"]?.GetValue<string>(),
                null,
                null,
                context["createdAt"]?.GetValue<string>(),
                null);
        }

        if (context["__typename"]?.GetValue<string>() != "CheckRun")
            return null;

        var status = context["status"]?.GetValue<string>();
        var conclusion = context["conclusion"]?.GetValue<string>();
        var checkState = status != "COMPLETED"
            ? "pending"
            : conclusion switch
            {
                "SUCCESS" => "success",
                "FAILURE" or "TIMED_OUT" or "STARTUP_FAILURE" or "ACTION_REQUIRED" => "failure",
                "SKIPPED" => "skipped",
                _ => "neutral",
            };

        return new GitHubCheckDto(
            context["name"]?.GetValue<string>() ?? string.Empty,
            checkState,
            context["detailsUrl"]?.GetValue<string>(),
            context["checkSuite"]?["workflowRun"]?["workflow"]?["name"]?.GetValue<string>(),
            context["databaseId"]?.GetValue<long>(),
            context["startedAt"]?.GetValue<string>(),
            context["completedAt"]?.GetValue<string>());
    }

    private static GitHubTimelineEntry? ParseTimelineEntry(JsonObject item)
    {
        var who = item["author"] ?? item["actor"];
        var login = who?["login"]?.GetValue<string>();
        var avatar = who?["avatarUrl"]?.GetValue<string>();
        var at = item["submittedAt"]?.GetValue<string>() ?? item["createdAt"]?.GetValue<string>() ?? string.Empty;

        switch (item["__typename"]?.GetValue<string>())
        {
            case "IssueComment":
                return new GitHubTimelineEntry("comment", login, avatar, at, item["body"]?.GetValue<string>(), null, item["url"]?.GetValue<string>(), null);
            case "PullRequestReview":
                var state = item["state"]?.GetValue<string>();
                var body = item["body"]?.GetValue<string>();
                // A bare "commented" review is the wrapper around inline comments; the threads show those.
                if (state == "COMMENTED" && string.IsNullOrWhiteSpace(body))
                    return null;
                return new GitHubTimelineEntry("review", login, avatar, at, body, state, item["url"]?.GetValue<string>(), null);
            case "MergedEvent":
                return new GitHubTimelineEntry("merged", login, avatar, at, null, null, null, null);
            case "ClosedEvent":
                return new GitHubTimelineEntry("closed", login, avatar, at, null, null, null, null);
            case "ReopenedEvent":
                return new GitHubTimelineEntry("reopened", login, avatar, at, null, null, null, null);
            case "ReadyForReviewEvent":
                return new GitHubTimelineEntry("ready", login, avatar, at, null, null, null, null);
            case "ConvertToDraftEvent":
                return new GitHubTimelineEntry("draft", login, avatar, at, null, null, null, null);
            case "CrossReferencedEvent":
                var source = item["source"];
                var number = source?["number"]?.GetValue<int>();
                if (number is null)
                    return null;
                var kind = source?["__typename"]?.GetValue<string>() == "PullRequest" ? "pull" : "issue";
                return new GitHubTimelineEntry(
                    "referenced",
                    login,
                    avatar,
                    at,
                    source?["title"]?.GetValue<string>(),
                    source?["state"]?.GetValue<string>()?.ToLowerInvariant(),
                    source?["url"]?.GetValue<string>(),
                    new GitHubReferenceDto(kind, source?["repository"]?["nameWithOwner"]?.GetValue<string>() ?? string.Empty, number.Value));
            default:
                return null;
        }
    }
}

// ── Response types (file-level so ApiJsonContext in the Api project can see them) ─────────────────

/// <summary>A label and its hex colour, without the <c>#</c>.</summary>
public sealed record GitHubLabelDto(string Name, string Color);

/// <summary>
/// Someone with a say on a pull request. <c>State</c> is their latest verdict (APPROVED, CHANGES_REQUESTED,
/// COMMENTED, DISMISSED), or REQUESTED while they haven't answered.
/// </summary>
public sealed record GitHubReviewerDto(string Login, string? AvatarUrl, string State);

/// <summary>A check on a pull request's head commit. <c>State</c>: success, failure, pending, skipped or neutral.</summary>
public sealed record GitHubCheckDto(
    string Name,
    string State,
    string? Url,
    string? WorkflowName,
    long? CheckRunId,
    string? StartedAt,
    string? CompletedAt);

/// <summary>
/// A pull request or issue as a list row shows it. The pull request fields are null for issues.
/// <c>State</c>: open, closed or merged. <c>Checks</c>: success, failure, pending or none.
/// <c>ReviewDecision</c>: APPROVED, CHANGES_REQUESTED, REVIEW_REQUIRED or null.
/// <c>Mergeable</c>: MERGEABLE, CONFLICTING or UNKNOWN.
/// </summary>
public sealed record GitHubItemSummary(
    string Kind,
    string Owner,
    string Repo,
    int Number,
    string Title,
    string Url,
    string State,
    bool IsDraft,
    string? Author,
    string? AuthorAvatarUrl,
    string CreatedAt,
    string UpdatedAt,
    int Comments,
    IReadOnlyList<GitHubLabelDto> Labels,
    string? HeadRef,
    string? BaseRef,
    int? Additions,
    int? Deletions,
    string? Checks,
    string? ReviewDecision,
    string? Mergeable,
    IReadOnlyList<GitHubReviewerDto> Reviewers,
    IReadOnlyList<string> Assignees);

/// <summary>One page of search results.</summary>
public sealed record GitHubItemPage(IReadOnlyList<GitHubItemSummary> Items, int TotalCount, string? EndCursor, bool HasNextPage);

/// <summary>Another pull request or issue, as a timeline entry points at it.</summary>
public sealed record GitHubReferenceDto(string Kind, string Repository, int Number);

/// <summary>
/// Something that happened on a pull request or issue. <c>Kind</c>: comment, review, merged, closed, reopened,
/// ready, draft or referenced. A review's <c>State</c> is its verdict; a reference's is the other item's state.
/// </summary>
public sealed record GitHubTimelineEntry(
    string Kind,
    string? Author,
    string? AuthorAvatarUrl,
    string CreatedAt,
    string? Body,
    string? State,
    string? Url,
    GitHubReferenceDto? Reference);

/// <summary>What a pull request or issue page shows. The pull request fields are null or empty for issues.</summary>
public sealed record GitHubItemDetail(
    GitHubItemSummary Summary,
    string? Body,
    int? ChangedFiles,
    string? MergedAt,
    string? MergedBy,
    IReadOnlyList<GitHubCheckDto> Checks,
    int UnresolvedThreads,
    IReadOnlyList<GitHubTimelineEntry> Timeline);

/// <summary>Open pull request and issue counts for a followed repository.</summary>
public sealed record GitHubRepoCounts(string FullName, int OpenPullRequests, int OpenIssues);

/// <summary>
/// The GitHub home: pull requests waiting on the user's review, the user's own open pull requests, and
/// issues assigned to them, across the repositories they follow.
/// </summary>
public sealed record GitHubWorkResponse(
    string? Login,
    IReadOnlyList<GitHubItemSummary> ReviewRequested,
    IReadOnlyList<GitHubItemSummary> Authored,
    IReadOnlyList<GitHubItemSummary> Assigned,
    IReadOnlyList<GitHubRepoCounts> Repos);
