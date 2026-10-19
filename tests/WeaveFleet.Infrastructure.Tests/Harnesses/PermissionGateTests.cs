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
    public async Task Dont_ask_again_allows_what_matches_the_harnesss_pattern_for_the_rest_of_the_session()
    {
        var gate = new PermissionGate { Policy = new PermissionPolicy(PermissionLevels.Ask) };

        await gate.AllowFromNowOnAsync(Ask("bash", "git push *"), default);

        gate.Decide("bash", ["git push origin other-branch"]).ShouldBe(PermissionReplies.Once);
        gate.Decide("bash", ["git push"]).ShouldBe(PermissionReplies.Once);
        gate.Decide("bash", ["git reset --hard"]).ShouldBeNull();
        gate.Decide("edit", ["src/app.ts"]).ShouldBeNull();
    }

    [Fact]
    public async Task Dont_ask_again_without_a_pattern_allows_the_whole_tool()
    {
        var gate = new PermissionGate { Policy = new PermissionPolicy(PermissionLevels.Ask) };

        await gate.AllowFromNowOnAsync(Ask("WebFetch"), default);

        gate.Decide("WebFetch", ["https://example.com"]).ShouldBe(PermissionReplies.Once);
        gate.Decide("webfetch", []).ShouldBe(PermissionReplies.Once);
    }

    [Fact]
    public async Task An_ask_matches_only_when_every_pattern_it_names_is_allowed()
    {
        var gate = new PermissionGate { Policy = new PermissionPolicy(PermissionLevels.Ask) };

        await gate.AllowFromNowOnAsync(Ask("bash", "ls *"), default);

        gate.Decide("bash", ["ls -la", "rm file"]).ShouldBeNull();
    }

    [Fact]
    public async Task A_run_nobody_watches_refuses_unless_the_user_allowed_it_before()
    {
        var gate = new PermissionGate { Policy = new PermissionPolicy(PermissionLevels.Ask, RejectAsks: true) };

        gate.Decide("bash", ["npm test"]).ShouldBe(PermissionReplies.Reject);
        await gate.AllowFromNowOnAsync(Ask("bash", "npm test"), default);
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

    private sealed class FakeRules : IPermissionRules
    {
        public List<WeaveFleet.Domain.Entities.SessionPermissionRule> Items { get; } = [];

        public IReadOnlyList<WeaveFleet.Domain.Entities.SessionPermissionRule> Current => Items;

        public int Loads { get; private set; }

        public Task LoadAsync(CancellationToken ct)
        {
            Loads++;
            return Task.CompletedTask;
        }

        public Task AddAsync(string tool, IReadOnlyList<string> patterns, CancellationToken ct)
        {
            Items.Add(new WeaveFleet.Domain.Entities.SessionPermissionRule
            {
                Id = $"rule_{Items.Count}",
                SessionId = "fleet-1",
                Kind = PermissionKinds.Classify(tool),
                Tool = tool,
                Patterns = patterns,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Rules_the_user_saved_before_apply_after_the_gate_is_rebuilt()
    {
        var rules = new FakeRules();
        var first = new PermissionGate { Policy = new PermissionPolicy(PermissionLevels.Ask) };
        await first.BindAsync(rules, default);
        await first.AllowFromNowOnAsync(Ask("bash", "bun test*"), default);

        // A restart builds a new gate over the same stored rules.
        var second = new PermissionGate { Policy = new PermissionPolicy(PermissionLevels.Ask) };
        await second.BindAsync(rules, default);

        rules.Loads.ShouldBe(2);
        second.Decide("bash", ["bun test src"]).ShouldBe(PermissionReplies.Once);
        second.Decide("bash", ["git push origin main"]).ShouldBeNull();
    }

    [Fact]
    public async Task A_rule_removed_from_the_list_asks_again_at_the_next_ask()
    {
        var rules = new FakeRules();
        var gate = new PermissionGate { Policy = new PermissionPolicy(PermissionLevels.Ask) };
        await gate.BindAsync(rules, default);
        await gate.AllowFromNowOnAsync(Ask("bash", "bun test*"), default);
        gate.Decide("bash", ["bun test"]).ShouldBe(PermissionReplies.Once);

        rules.Items.Clear();

        gate.Decide("bash", ["bun test"]).ShouldBeNull();
    }

    [Fact]
    public async Task What_was_clicked_before_the_rules_were_bound_is_kept_in_them()
    {
        var gate = new PermissionGate { Policy = new PermissionPolicy(PermissionLevels.Ask) };
        await gate.AllowFromNowOnAsync(Ask("bash", "ls *", "pwd"), default);
        var rules = new FakeRules();

        await gate.BindAsync(rules, default);

        rules.Items.Single().Patterns.ShouldBe(["ls *", "pwd"]);
        gate.Decide("bash", ["ls -la"]).ShouldBe(PermissionReplies.Once);
    }
}
