using System.Text.Json.Nodes;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

namespace WeaveFleet.Infrastructure.Tests.Plugins.BuiltIn.GitHub;

public sealed class SmartLinkWatcherMappingTests
{
    private static readonly GitHubLinkReference Pr = new("owner", "repo", 187, GitHubLinkReference.PullRequest);

    private static JsonObject PullRequest(string state = "open", bool merged = false, bool draft = false, string head = "feat/x", bool? mergeable = true)
        => (JsonObject)JsonNode.Parse($$"""
            {
              "number": 187,
              "title": "feat(client): #1 mock API",
              "state": "{{state}}",
              "merged": {{(merged ? "true" : "false")}},
              "draft": {{(draft ? "true" : "false")}},
              "mergeable": {{(mergeable is null ? "null" : mergeable.Value ? "true" : "false")}},
              "html_url": "https://github.com/owner/repo/pull/187",
              "head": { "ref": "{{head}}", "sha": "abc" },
              "base": { "ref": "main" },
              "labels": [ { "name": "client", "color": "1d76db", "id": 5 } ]
            }
            """)!;

    [Theory]
    [InlineData("open", false, false, "open", false)]
    [InlineData("open", false, true, "draft", false)]
    [InlineData("closed", true, false, "merged", true)]
    [InlineData("closed", false, false, "closed", true)]
    public void ApplyPullRequest_maps_state(string state, bool merged, bool draft, string expectedStatus, bool terminal)
    {
        var link = new SmartLink { Relationship = SmartLinkRelationships.Mentioned };
        var metadata = new JsonObject();

        SmartLinkWatcherService.ApplyPullRequest(link, Pr, PullRequest(state, merged, draft), metadata, sessionBranch: null);

        link.Status.ShouldBe(expectedStatus);
        link.IsTerminal.ShouldBe(terminal);
        link.ResourceType.ShouldBe("pull_request");
        link.Title.ShouldBe("owner/repo #187: feat(client): #1 mock API");
        metadata["labels"]!.AsArray().ShouldHaveSingleItem()!["name"]!.GetValue<string>().ShouldBe("client");
        metadata["headRef"]!.GetValue<string>().ShouldBe("feat/x");
    }

    [Fact]
    public void ApplyPullRequest_makes_a_pull_request_from_the_sessions_branch_its_own()
    {
        var mentioned = new SmartLink { Relationship = SmartLinkRelationships.Mentioned };
        SmartLinkWatcherService.ApplyPullRequest(mentioned, Pr, PullRequest(head: "feat/x"), new JsonObject(), sessionBranch: "feat/x");
        mentioned.Relationship.ShouldBe(SmartLinkRelationships.Own);

        var origin = new SmartLink { Relationship = SmartLinkRelationships.Origin };
        SmartLinkWatcherService.ApplyPullRequest(origin, Pr, PullRequest(head: "feat/x"), new JsonObject(), sessionBranch: "feat/x");
        origin.Relationship.ShouldBe(SmartLinkRelationships.Origin);

        var other = new SmartLink { Relationship = SmartLinkRelationships.Mentioned };
        SmartLinkWatcherService.ApplyPullRequest(other, Pr, PullRequest(head: "feat/y"), new JsonObject(), sessionBranch: "feat/x");
        other.Relationship.ShouldBe(SmartLinkRelationships.Mentioned);
    }

    [Fact]
    public void ApplyPullRequest_keeps_existing_ci_failures_and_records_unknown_mergeability_as_null()
    {
        var metadata = new JsonObject { ["ciFailures"] = new JsonArray(new JsonObject { ["sha"] = "abc", ["checkRunName"] = "e2e" }) };

        SmartLinkWatcherService.ApplyPullRequest(new SmartLink(), Pr, PullRequest(mergeable: null), metadata, null);

        metadata["ciFailures"]!.AsArray().Count.ShouldBe(1);
        metadata.ContainsKey("mergeable").ShouldBeTrue();
        metadata["mergeable"].ShouldBeNull();
    }

    [Fact]
    public void ApplyIssue_maps_state_and_title()
    {
        var link = new SmartLink();
        var issue = (JsonObject)JsonNode.Parse("""{ "title": "Add mock API support", "state": "closed", "labels": [] }""")!;

        SmartLinkWatcherService.ApplyIssue(link, new GitHubLinkReference("owner", "repo", 42, GitHubLinkReference.Issue), issue, new JsonObject());

        link.Status.ShouldBe("closed");
        link.StatusLabel.ShouldBe("Closed");
        link.IsTerminal.ShouldBeTrue();
        link.Title.ShouldBe("owner/repo #42: Add mock API support");
    }

    [Theory]
    [InlineData("git@github.com:pgermishuys/weave-fleet.git", "pgermishuys", "weave-fleet")]
    [InlineData("https://github.com/pgermishuys/weave-fleet", "pgermishuys", "weave-fleet")]
    [InlineData("https://github.com/pgermishuys/weave.fleet.git/", "pgermishuys", "weave.fleet")]
    [InlineData("ssh://git@github.com/owner/repo.git", "owner", "repo")]
    public void ParseGitHubRemote_reads_owner_and_repo(string remote, string owner, string repo)
    {
        SmartLinkWatcherService.ParseGitHubRemote(remote).ShouldBe((owner, repo));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("git@gitlab.com:owner/repo.git")]
    public void ParseGitHubRemote_ignores_other_hosts(string? remote)
    {
        SmartLinkWatcherService.ParseGitHubRemote(remote).ShouldBeNull();
    }
}
