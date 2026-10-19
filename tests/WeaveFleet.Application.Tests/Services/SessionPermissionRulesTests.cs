using Shouldly;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>The "Don't ask again" rules a session keeps: listed, removed, copied, and announced on its topic.</summary>
public sealed class SessionPermissionRulesTests
{
    private sealed class MemoryRepository : ISessionPermissionRuleRepository
    {
        public List<SessionPermissionRule> Rows { get; } = [];

        public Task<IReadOnlyList<SessionPermissionRule>> ListAsync(string sessionId)
            => Task.FromResult<IReadOnlyList<SessionPermissionRule>>([.. Rows.Where(r => r.SessionId == sessionId)]);

        public Task<bool> AddAsync(SessionPermissionRule rule)
        {
            Rows.Add(rule);
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(string sessionId, string ruleId)
            => Task.FromResult(Rows.RemoveAll(r => r.SessionId == sessionId && r.Id == ruleId) > 0);

        public Task<int> DeleteAllAsync(string sessionId) => Task.FromResult(Rows.RemoveAll(r => r.SessionId == sessionId));

        public Task CopyAsync(string fromSessionId, string toSessionId)
        {
            Rows.AddRange(Rows.Where(r => r.SessionId == fromSessionId).Select(r => r with { Id = r.Id + "-copy", SessionId = toSessionId }).ToList());
            return Task.CompletedTask;
        }

        public Task<string?> OwnerAsync(string sessionId) => Task.FromResult<string?>("local-user");
    }

    private static (SessionPermissionRules Rules, MemoryRepository Repository, FakeEventBroadcaster Broadcaster) Create()
    {
        var repository = new MemoryRepository();
        var broadcaster = new FakeEventBroadcaster();
        return (new SessionPermissionRules(repository, broadcaster), repository, broadcaster);
    }

    [Fact]
    public async Task A_rule_added_shows_in_the_list_and_goes_out_on_the_sessions_topic()
    {
        var (rules, _, broadcaster) = Create();

        await rules.AddAsync("s1", "bash", ["bun test*"]);

        var rule = (await rules.ListAsync("s1")).ShouldHaveSingleItem();
        rule.Kind.ShouldBe("shell");
        rule.Patterns.ShouldBe(["bun test*"]);
        var sent = broadcaster.Broadcasts.Single();
        sent.Topic.ShouldBe("session:s1");
        sent.Type.ShouldBe(SessionPermissionRules.ChangedEvent);
        sent.UserId.ShouldBe("local-user");
    }

    [Fact]
    public async Task A_rule_with_no_patterns_allows_the_whole_tool()
    {
        var (rules, _, _) = Create();

        await rules.AddAsync("s1", "WebFetch", []);

        (await rules.ListAsync("s1")).Single().Patterns.ShouldBe(["*"]);
    }

    [Fact]
    public async Task A_removed_rule_leaves_what_the_gate_reads_at_once()
    {
        var (rules, _, broadcaster) = Create();
        await rules.AddAsync("s1", "bash", ["bun test*"]);
        var bound = rules.ForSession("s1");
        await bound.LoadAsync(default);
        bound.Current.Count.ShouldBe(1);

        (await rules.RemoveAsync("s1", bound.Current[0].Id)).ShouldBeTrue();

        bound.Current.ShouldBeEmpty();
        broadcaster.Broadcasts.Count.ShouldBe(2);
        (await rules.RemoveAsync("s1", "nope")).ShouldBeFalse();
    }

    [Fact]
    public async Task Clearing_removes_every_rule_of_that_session_only()
    {
        var (rules, _, _) = Create();
        await rules.AddAsync("s1", "bash", ["a*"]);
        await rules.AddAsync("s1", "edit", []);
        await rules.AddAsync("s2", "bash", ["b*"]);

        await rules.ClearAsync("s1");

        (await rules.ListAsync("s1")).ShouldBeEmpty();
        (await rules.ListAsync("s2")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_fork_keeps_the_parents_rules()
    {
        var (rules, _, broadcaster) = Create();
        await rules.AddAsync("parent", "bash", ["bun test*"]);
        broadcaster.Broadcasts.Clear();

        await rules.CopyAsync("parent", "fork");

        (await rules.ListAsync("fork")).Single().Patterns.ShouldBe(["bun test*"]);
        broadcaster.Broadcasts.Single().Topic.ShouldBe("session:fork");
    }
}
