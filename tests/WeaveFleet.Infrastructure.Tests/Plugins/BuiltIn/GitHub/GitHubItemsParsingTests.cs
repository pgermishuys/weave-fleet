using System.Text.Json.Nodes;
using WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

namespace WeaveFleet.Infrastructure.Tests.Plugins.BuiltIn.GitHub;

/// <summary>How GitHub's GraphQL answers become the rows and pages Fleet shows.</summary>
public sealed class GitHubItemsParsingTests
{
    private const string PullRequestNode = """
        {
          "__typename": "PullRequest",
          "number": 314,
          "title": "feat(sessions): the queue Fleet keeps",
          "url": "https://github.com/acme/rocket/pull/314",
          "state": "OPEN",
          "isDraft": false,
          "createdAt": "2026-09-25T09:00:00Z",
          "updatedAt": "2026-09-25T12:00:00Z",
          "author": { "login": "pat", "avatarUrl": "https://avatars/pat" },
          "repository": { "name": "rocket", "owner": { "login": "acme" } },
          "comments": { "totalCount": 3 },
          "labels": { "nodes": [ { "name": "client", "color": "1d76db" } ] },
          "headRefName": "feat/server-side-queue",
          "baseRefName": "main",
          "additions": 412,
          "deletions": 88,
          "changedFiles": 14,
          "mergeable": "CONFLICTING",
          "reviewDecision": "CHANGES_REQUESTED",
          "latestOpinionatedReviews": { "nodes": [
            { "state": "APPROVED", "author": { "login": "tvdb", "avatarUrl": "https://avatars/tvdb" } },
            { "state": "CHANGES_REQUESTED", "author": { "login": "sarah", "avatarUrl": null } }
          ] },
          "reviewRequests": { "nodes": [
            { "requestedReviewer": { "__typename": "User", "login": "sarah", "avatarUrl": null } },
            { "requestedReviewer": { "__typename": "User", "login": "kim", "avatarUrl": "https://avatars/kim" } },
            { "requestedReviewer": { "__typename": "Team", "name": "core" } }
          ] },
          "commits": { "nodes": [ { "commit": { "statusCheckRollup": { "state": "FAILURE" } } } ] }
        }
        """;

    private const string IssueNode = """
        {
          "__typename": "Issue",
          "number": 318,
          "title": "Session list flickers",
          "url": "https://github.com/acme/rocket/issues/318",
          "state": "OPEN",
          "createdAt": "2026-09-25T08:00:00Z",
          "updatedAt": "2026-09-25T08:30:00Z",
          "author": { "login": "tvdb", "avatarUrl": "https://avatars/tvdb" },
          "repository": { "name": "rocket", "owner": { "login": "acme" } },
          "comments": { "totalCount": 6 },
          "labels": { "nodes": [ { "name": "bug", "color": "d73a4a" } ] },
          "assignees": { "nodes": [ { "login": "pat" } ] }
        }
        """;

    [Fact]
    public void ParseSearch_reads_pull_requests_and_issues_with_their_page()
    {
        var response = JsonNode.Parse($$"""
            { "data": { "search": {
              "issueCount": 42,
              "pageInfo": { "endCursor": "Y3Vyc29y", "hasNextPage": true },
              "nodes": [ {{PullRequestNode}}, {{IssueNode}}, {} ]
            } } }
            """);

        var page = GitHubItems.ParseSearch(response);

        page.TotalCount.ShouldBe(42);
        page.EndCursor.ShouldBe("Y3Vyc29y");
        page.HasNextPage.ShouldBeTrue();
        page.Items.Count.ShouldBe(2);

        var pull = page.Items[0];
        pull.Kind.ShouldBe("pull");
        pull.Owner.ShouldBe("acme");
        pull.Repo.ShouldBe("rocket");
        pull.Number.ShouldBe(314);
        pull.State.ShouldBe("open");
        pull.Checks.ShouldBe("failure");
        pull.ReviewDecision.ShouldBe("CHANGES_REQUESTED");
        pull.Mergeable.ShouldBe("CONFLICTING");
        pull.Additions.ShouldBe(412);
        pull.Deletions.ShouldBe(88);
        pull.HeadRef.ShouldBe("feat/server-side-queue");
        pull.Labels.ShouldHaveSingleItem().ShouldBe(new GitHubLabelDto("client", "1d76db"));

        var issue = page.Items[1];
        issue.Kind.ShouldBe("issue");
        issue.Checks.ShouldBeNull();
        issue.Additions.ShouldBeNull();
        issue.Reviewers.ShouldBeEmpty();
        issue.Assignees.ShouldBe(["pat"]);
        issue.Comments.ShouldBe(6);
    }

    [Fact]
    public void ParseReviewers_keeps_each_verdict_then_adds_reviewers_who_havent_answered()
    {
        var reviewers = GitHubItems.ParseReviewers(JsonNode.Parse(PullRequestNode)!);

        reviewers.Select(r => (r.Login, r.State)).ShouldBe(
        [
            ("tvdb", "APPROVED"),
            ("sarah", "CHANGES_REQUESTED"),
            ("kim", "REQUESTED"),
            ("core", "REQUESTED"),
        ]);
    }

    [Theory]
    [InlineData("SUCCESS", "success")]
    [InlineData("FAILURE", "failure")]
    [InlineData("ERROR", "failure")]
    [InlineData("PENDING", "pending")]
    [InlineData("EXPECTED", "pending")]
    [InlineData(null, "none")]
    public void ChecksRollup_maps_githubs_states(string? state, string expected)
        => GitHubItems.ChecksRollup(state).ShouldBe(expected);

    [Fact]
    public void ParseDetail_reads_the_body_checks_open_threads_and_timeline()
    {
        var node = JsonNode.Parse(PullRequestNode)!.AsObject();
        node["body"] = "## Summary";
        node["mergedAt"] = null;
        node["reviewThreads"] = JsonNode.Parse("""{ "nodes": [ { "isResolved": false, "isOutdated": false }, { "isResolved": true, "isOutdated": false }, { "isResolved": false, "isOutdated": true } ] }""");
        node["commits"] = JsonNode.Parse("""
            { "nodes": [ { "commit": { "statusCheckRollup": { "state": "FAILURE", "contexts": { "nodes": [
              { "__typename": "CheckRun", "name": "client-tests", "status": "COMPLETED", "conclusion": "FAILURE", "detailsUrl": "https://ci/1", "databaseId": 11, "startedAt": "a", "completedAt": "b", "checkSuite": { "workflowRun": { "workflow": { "name": "CI" } } } },
              { "__typename": "CheckRun", "name": "e2e", "status": "IN_PROGRESS", "conclusion": null, "detailsUrl": null, "databaseId": 12, "startedAt": "a", "completedAt": null, "checkSuite": null },
              { "__typename": "CheckRun", "name": "docs", "status": "COMPLETED", "conclusion": "SKIPPED", "detailsUrl": null, "databaseId": 13, "startedAt": null, "completedAt": null, "checkSuite": null },
              { "__typename": "StatusContext", "context": "deploy/preview", "state": "SUCCESS", "targetUrl": "https://preview", "createdAt": "c" }
            ] } } } } ] }
            """);
        node["timelineItems"] = JsonNode.Parse("""
            { "nodes": [
              { "__typename": "PullRequestReview", "author": { "login": "tvdb", "avatarUrl": null }, "submittedAt": "t1", "createdAt": "t0", "body": "", "state": "APPROVED", "url": "u1" },
              { "__typename": "PullRequestReview", "author": { "login": "sarah", "avatarUrl": null }, "submittedAt": "t2", "createdAt": "t2", "body": "", "state": "COMMENTED", "url": "u2" },
              { "__typename": "IssueComment", "author": { "login": "sarah", "avatarUrl": null }, "createdAt": "t3", "body": "Does it drain?", "url": "u3" },
              { "__typename": "CrossReferencedEvent", "actor": { "login": "pat", "avatarUrl": null }, "createdAt": "t4",
                "source": { "__typename": "Issue", "number": 318, "title": "Flicker", "url": "u4", "state": "OPEN", "repository": { "nameWithOwner": "acme/rocket" } } },
              { "__typename": "MergedEvent", "actor": { "login": "pat", "avatarUrl": null }, "createdAt": "t5" },
              { "__typename": "SomethingNew" }
            ] }
            """);
        var response = new JsonObject { ["data"] = new JsonObject { ["repository"] = new JsonObject { ["issueOrPullRequest"] = node } } };

        var detail = GitHubItems.ParseDetail(response).ShouldNotBeNull();

        detail.Body.ShouldBe("## Summary");
        detail.ChangedFiles.ShouldBe(14);
        detail.UnresolvedThreads.ShouldBe(1);
        detail.Checks.Select(c => (c.Name, c.State)).ShouldBe(
        [
            ("client-tests", "failure"),
            ("e2e", "pending"),
            ("docs", "skipped"),
            ("deploy/preview", "success"),
        ]);
        detail.Checks[0].WorkflowName.ShouldBe("CI");
        detail.Checks[0].CheckRunId.ShouldBe(11);
        // A bare "commented" review only wraps inline comments, which the threads show.
        detail.Timeline.Select(e => e.Kind).ShouldBe(["review", "comment", "referenced", "merged"]);
        detail.Timeline[2].Reference.ShouldBe(new GitHubReferenceDto("issue", "acme/rocket", 318));
        detail.Timeline[2].State.ShouldBe("open");
    }

    [Fact]
    public void ParseDetail_leaves_out_the_close_that_comes_with_a_merge()
    {
        var node = JsonNode.Parse(PullRequestNode)!.AsObject();
        node["state"] = "MERGED";
        node["timelineItems"] = JsonNode.Parse("""
            { "nodes": [
              { "__typename": "MergedEvent", "actor": { "login": "pat", "avatarUrl": null }, "createdAt": "t1" },
              { "__typename": "ClosedEvent", "actor": { "login": "pat", "avatarUrl": null }, "createdAt": "t1" }
            ] }
            """);
        var response = new JsonObject { ["data"] = new JsonObject { ["repository"] = new JsonObject { ["issueOrPullRequest"] = node } } };

        GitHubItems.ParseDetail(response)!.Timeline.Select(e => e.Kind).ShouldBe(["merged"]);
    }

    [Fact]
    public void ParseDetail_returns_null_when_github_found_nothing()
        => GitHubItems.ParseDetail(JsonNode.Parse("""{ "data": { "repository": { "issueOrPullRequest": null } }, "errors": [ { "message": "Could not resolve" } ] }""")).ShouldBeNull();

    [Fact]
    public void BuildNumbersQuery_asks_for_each_number_once_and_ParseNumbers_keeps_the_order()
    {
        var (query, variables) = GitHubItems.BuildNumbersQuery("acme", "rocket", [314, 318]);

        query.ShouldContain("i0: issueOrPullRequest(number: 314)");
        query.ShouldContain("i1: issueOrPullRequest(number: 318)");
        variables["owner"]!.GetValue<string>().ShouldBe("acme");

        var response = JsonNode.Parse($$"""{ "data": { "repository": { "i0": {{PullRequestNode}}, "i1": null } } }""");
        GitHubItems.ParseNumbers(response, 2).ShouldHaveSingleItem().Number.ShouldBe(314);
    }

    [Fact]
    public void Repo_counts_come_back_per_followed_repository()
    {
        var (query, variables) = GitHubItems.BuildRepoCountsQuery([("acme", "rocket"), ("acme", "api")]);

        query.ShouldContain("$o0: String!, $n0: String!, $o1: String!, $n1: String!");
        query.ShouldContain("r1: repository(owner: $o1, name: $n1)");
        variables["n1"]!.GetValue<string>().ShouldBe("api");

        var counts = GitHubItems.ParseRepoCounts(JsonNode.Parse("""
            { "data": {
              "r0": { "nameWithOwner": "acme/rocket", "pullRequests": { "totalCount": 5 }, "issues": { "totalCount": 6 } },
              "r1": null
            } }
            """));

        counts.ShouldHaveSingleItem().ShouldBe(new GitHubRepoCounts("acme/rocket", 5, 6));
    }

    [Fact]
    public void ParseWork_reads_the_viewer_and_each_list()
    {
        var response = JsonNode.Parse($$"""
            { "data": {
              "viewer": { "login": "pat", "avatarUrl": "https://avatars/pat" },
              "review": { "nodes": [ {{PullRequestNode}} ] },
              "authored": { "nodes": [] },
              "assigned": { "nodes": [ {{IssueNode}} ] }
            } }
            """);

        var work = GitHubItems.ParseWork(response, []);

        work.Login.ShouldBe("pat");
        work.AvatarUrl.ShouldBe("https://avatars/pat");
        work.ReviewRequested.ShouldHaveSingleItem().Number.ShouldBe(314);
        work.Authored.ShouldBeEmpty();
        work.Assigned.ShouldHaveSingleItem().Kind.ShouldBe("issue");
    }

    [Fact]
    public void ErrorMessage_reads_the_first_graphql_error()
        => GitHubItems.ErrorMessage(JsonNode.Parse("""{ "errors": [ { "message": "Bad credentials" }, { "message": "second" } ] }""")).ShouldBe("Bad credentials");
}
