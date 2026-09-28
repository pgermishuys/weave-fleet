using Shouldly;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

public sealed class PermissionGateTests
{
    private static PermissionAsk Ask(string tool, params string[] always) => new()
    {
        Id = "per_1",
        SessionId = "fleet-1",
        Kind = PermissionKinds.Classify(tool),
        Tool = tool,
        Always = always,
    };

    [Fact]
    public void Until_fleet_says_otherwise_everything_runs()
        => new PermissionGate().Decide("bash", ["rm -rf build"]).ShouldBe(PermissionReplies.Once);

    [Fact]
    public void What_the_level_doesnt_allow_goes_to_the_user()
    {
        var gate = new PermissionGate { Policy = new PermissionPolicy(PermissionLevels.Ask) };

        gate.Decide("bash", ["git push origin main"]).ShouldBeNull();
        gate.Decide("read", ["src/app.ts"]).ShouldBe(PermissionReplies.Once);
    }

    [Fact]
    public void Dont_ask_again_allows_what_matches_the_harnesss_pattern_for_the_rest_of_the_session()
    {
        var gate = new PermissionGate { Policy = new PermissionPolicy(PermissionLevels.Ask) };

        gate.AllowFromNowOn(Ask("bash", "git push *"));

        gate.Decide("bash", ["git push origin other-branch"]).ShouldBe(PermissionReplies.Once);
        gate.Decide("bash", ["git push"]).ShouldBe(PermissionReplies.Once);
        gate.Decide("bash", ["git reset --hard"]).ShouldBeNull();
        gate.Decide("edit", ["src/app.ts"]).ShouldBeNull();
    }

    [Fact]
    public void Dont_ask_again_without_a_pattern_allows_the_whole_tool()
    {
        var gate = new PermissionGate { Policy = new PermissionPolicy(PermissionLevels.Ask) };

        gate.AllowFromNowOn(Ask("WebFetch"));

        gate.Decide("WebFetch", ["https://example.com"]).ShouldBe(PermissionReplies.Once);
        gate.Decide("webfetch", []).ShouldBe(PermissionReplies.Once);
    }

    [Fact]
    public void An_ask_matches_only_when_every_pattern_it_names_is_allowed()
    {
        var gate = new PermissionGate { Policy = new PermissionPolicy(PermissionLevels.Ask) };

        gate.AllowFromNowOn(Ask("bash", "ls *"));

        gate.Decide("bash", ["ls -la", "rm file"]).ShouldBeNull();
    }

    [Fact]
    public void A_run_nobody_watches_refuses_unless_the_user_allowed_it_before()
    {
        var gate = new PermissionGate { Policy = new PermissionPolicy(PermissionLevels.Ask, RejectAsks: true) };

        gate.Decide("bash", ["npm test"]).ShouldBe(PermissionReplies.Reject);
        gate.AllowFromNowOn(Ask("bash", "npm test"));
        gate.Decide("bash", ["npm test"]).ShouldBe(PermissionReplies.Once);
    }

    [Theory]
    [InlineData("git push origin main", "git push *", true)]
    [InlineData("git push", "git push *", true)]
    [InlineData("git pushy", "git push *", false)]
    [InlineData("src/a.ts", "src/*.ts", true)]
    [InlineData("a+b (c)", "a+b (c)", true)]
    [InlineData("anything at all", "*", true)]
    [InlineData("ab", "a?", true)]
    [InlineData("abc", "a?", false)]
    public void Wildcards_match_as_opencodes_do(string value, string pattern, bool matches)
        => Wildcard.Matches(value, pattern).ShouldBe(matches);
}
