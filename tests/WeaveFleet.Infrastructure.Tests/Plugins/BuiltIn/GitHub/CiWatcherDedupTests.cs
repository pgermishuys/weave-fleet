using System.Text.Json.Nodes;
using WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

namespace WeaveFleet.Infrastructure.Tests.Plugins.BuiltIn.GitHub;

public sealed class CiWatcherDedupTests
{
    [Fact]
    public void build_ci_status_response_returns_none_when_no_check_runs()
    {
        var result = GitHubEndpointMappings.BuildCiStatusResponse("abc123", null);

        result.HeadSha.ShouldBe("abc123");
        result.CiStatus.ShouldBe("none");
        result.CheckRuns.ShouldBeEmpty();
    }

    [Fact]
    public void build_ci_status_response_returns_success_when_all_pass()
    {
        var checkRuns = BuildCheckRunsNode(
            ("build", "completed", "success"),
            ("tests", "completed", "success"));

        var result = GitHubEndpointMappings.BuildCiStatusResponse("abc123", checkRuns);

        result.CiStatus.ShouldBe("success");
        result.CheckRuns.Count.ShouldBe(2);
    }

    [Fact]
    public void build_ci_status_response_returns_failure_when_any_fails()
    {
        var checkRuns = BuildCheckRunsNode(
            ("build", "completed", "success"),
            ("tests", "completed", "failure"));

        var result = GitHubEndpointMappings.BuildCiStatusResponse("abc123", checkRuns);

        result.CiStatus.ShouldBe("failure");
    }

    [Fact]
    public void build_ci_status_response_returns_pending_when_in_progress()
    {
        var checkRuns = BuildCheckRunsNode(
            ("build", "in_progress", null),
            ("tests", "completed", "success"));

        var result = GitHubEndpointMappings.BuildCiStatusResponse("abc123", checkRuns);

        result.CiStatus.ShouldBe("pending");
    }

    [Fact]
    public void build_ci_status_response_failure_takes_priority_over_pending()
    {
        var checkRuns = BuildCheckRunsNode(
            ("build", "completed", "failure"),
            ("tests", "in_progress", null));

        var result = GitHubEndpointMappings.BuildCiStatusResponse("abc123", checkRuns);

        // Failure takes priority
        result.CiStatus.ShouldBe("failure");
    }

    [Fact]
    public void build_ci_status_response_maps_check_run_fields_correctly()
    {
        var checkRunsNode = new JsonObject
        {
            ["check_runs"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = JsonValue.Create(12345L),
                    ["name"] = JsonValue.Create("build"),
                    ["status"] = JsonValue.Create("completed"),
                    ["conclusion"] = JsonValue.Create("failure"),
                    ["html_url"] = JsonValue.Create("https://github.com/actions/run/1"),
                    ["app"] = new JsonObject { ["slug"] = JsonValue.Create("github-actions") },
                    ["started_at"] = JsonValue.Create("2024-01-01T00:00:00Z"),
                    ["completed_at"] = JsonValue.Create("2024-01-01T00:05:00Z"),
                },
            },
        };

        var result = GitHubEndpointMappings.BuildCiStatusResponse("sha1", checkRunsNode);

        var cr = result.CheckRuns[0];
        cr.Id.ShouldBe(12345L);
        cr.Name.ShouldBe("build");
        cr.Status.ShouldBe("completed");
        cr.Conclusion.ShouldBe("failure");
        cr.HtmlUrl.ShouldBe("https://github.com/actions/run/1");
        cr.WorkflowName.ShouldBe("github-actions");
        cr.StartedAt.ShouldBe("2024-01-01T00:00:00Z");
        cr.CompletedAt.ShouldBe("2024-01-01T00:05:00Z");
    }

    private static JsonObject BuildCheckRunsNode(params (string name, string status, string? conclusion)[] runs)
    {
        var arr = new JsonArray();
        foreach (var (name, status, conclusion) in runs)
        {
            var obj = new JsonObject
            {
                ["id"] = JsonValue.Create(1L),
                ["name"] = JsonValue.Create(name),
                ["status"] = JsonValue.Create(status),
                ["html_url"] = JsonValue.Create("https://github.com"),
            };
            if (conclusion is not null)
                obj["conclusion"] = JsonValue.Create(conclusion);
            arr.Add((JsonNode)obj);
        }
        return new JsonObject { ["check_runs"] = arr };
    }
}
