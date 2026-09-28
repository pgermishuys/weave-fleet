using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Sessions;

public sealed class SessionPermissionsTests
{
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly InMemorySessionRepository _sessions = new();

    [Fact]
    public async Task Nothing_asks_until_the_user_picks_a_level()
        => (await ResolveAsync(Session("opencode"))).ShouldBe(PermissionPolicy.AllowAll);

    [Fact]
    public async Task Every_harness_uses_the_default_level()
    {
        _preferences.Seed(SessionPermissions.LevelKey, PermissionLevels.Ask);

        (await ResolveAsync(Session("opencode2"))).ShouldBe(new PermissionPolicy(PermissionLevels.Ask));
    }

    [Fact]
    public async Task A_harnesss_own_level_wins_over_the_default_and_an_empty_one_means_the_default()
    {
        _preferences.Seed(SessionPermissions.LevelKey, PermissionLevels.Ask);
        _preferences.Seed(SessionPermissions.HarnessLevelKey("claude-code"), PermissionLevels.Edits);
        _preferences.Seed(SessionPermissions.HarnessLevelKey("opencode"), "");

        (await ResolveAsync(Session("claude-code"))).Level.ShouldBe(PermissionLevels.Edits);
        (await ResolveAsync(Session("opencode"))).Level.ShouldBe(PermissionLevels.Ask);
    }

    [Fact]
    public async Task A_run_nobody_watches_asks_nothing_unless_the_user_says_otherwise()
    {
        _preferences.Seed(SessionPermissions.LevelKey, PermissionLevels.Ask);
        var automation = Session("opencode", source: "automation:auto-1");
        var step = Session("opencode", workflowRunId: "run-1");

        (await ResolveAsync(automation)).ShouldBe(PermissionPolicy.AllowAll);
        (await ResolveAsync(step)).ShouldBe(PermissionPolicy.AllowAll);

        _preferences.Seed(SessionPermissions.UnattendedKey, UnattendedPermissions.Same);
        (await ResolveAsync(automation)).ShouldBe(new PermissionPolicy(PermissionLevels.Ask));

        _preferences.Seed(SessionPermissions.UnattendedKey, UnattendedPermissions.Deny);
        (await ResolveAsync(step)).ShouldBe(new PermissionPolicy(PermissionLevels.Ask, RejectAsks: true));
    }

    [Fact]
    public async Task A_workflow_step_the_user_finishes_is_watched()
    {
        _preferences.Seed(SessionPermissions.LevelKey, PermissionLevels.Ask);

        (await ResolveAsync(Session("opencode", workflowRunId: "run-1", userFinishes: true)))
            .ShouldBe(new PermissionPolicy(PermissionLevels.Ask));
    }

    [Fact]
    public async Task A_subagent_is_as_watched_as_the_session_it_works_for()
    {
        _preferences.Seed(SessionPermissions.LevelKey, PermissionLevels.Ask);
        var automation = Session("opencode", source: "automation:auto-1");
        await _sessions.InsertAsync(automation);
        var child = Session("opencode");
        child.ParentSessionId = automation.Id;

        (await ResolveAsync(child)).ShouldBe(PermissionPolicy.AllowAll);
    }

    private Task<PermissionPolicy> ResolveAsync(Session session) => SessionPermissions.ResolveAsync(_preferences, _sessions, session);

    private static Session Session(string harness, string? source = null, string? workflowRunId = null, bool userFinishes = false) => new()
    {
        Id = Guid.NewGuid().ToString(),
        HarnessType = harness,
        SourceReference = source,
        WorkflowRunId = workflowRunId,
        WorkflowUserFinishes = userFinishes,
    };
}
