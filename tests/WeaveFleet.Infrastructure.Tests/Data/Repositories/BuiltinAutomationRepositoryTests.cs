using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

/// <summary>What a night of Dreaming needs from the database: sessions since a time, the built-in automation, and its run's summary.</summary>
public sealed class BuiltinAutomationRepositoryTests
{
    private const string OwnerId = TestUserContext.DefaultUserId;

    [Fact]
    public async Task ListCreatedSince_returns_own_top_level_sessions_from_that_time_newest_first_up_to_the_limit()
    {
        var (conn, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = conn;
        var repo = new SessionRepository(factory, new TestUserContext());
        var graph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "someone-else");
        // The helper's sessions are made now; the ones below are earlier, so only they are compared with the time.
        Session Make(string id, string createdAt, Action<Session>? change = null)
        {
            var session = new Session
            {
                Id = id, WorkspaceId = graph.Workspace.Id, InstanceId = graph.Instance.Id, OpencodeSessionId = "oc-" + id, Title = id, Status = "active",
                Directory = "/tmp/ws", CreatedAt = createdAt, UserId = OwnerId, IsHidden = id.StartsWith("hidden", StringComparison.Ordinal),
            };
            change?.Invoke(session);
            return session;
        }

        await repo.InsertAsync(Make("older", "2026-10-26T01:00:00.0000000Z"));
        await repo.InsertAsync(Make("at", "2026-10-26T12:00:00.0000000Z"));
        await repo.InsertAsync(Make("hidden-top-level", "2026-10-26T20:00:00.0000000Z"));
        await repo.InsertAsync(Make("later", "2026-10-27T06:00:00.0000000Z"));
        await repo.InsertAsync(Make("child", "2026-10-27T07:00:00.0000000Z", s => s.ParentSessionId = "later"));
        await repo.InsertAsync(Make("side", "2026-10-27T07:30:00.0000000Z", s => s.SideOfSessionId = "later"));

        var since = (await repo.ListCreatedSinceAsync("2026-10-26T12:00:00.0000000Z", 10)).Select(s => s.Id).ToList();
        var limited = await repo.ListCreatedSinceAsync("2026-10-26T12:00:00.0000000Z", 2);

        since.Where(id => id is "later" or "hidden-top-level" or "at" or "older" or "child" or "side")
            .ShouldBe(["later", "hidden-top-level", "at"]);
        since.ShouldNotContain(graph.Session.Id.Replace(graph.Session.Id, "never"));
        since.Count.ShouldBe(since.Distinct().Count());
        limited.Count.ShouldBe(2);
        (await repo.ListCreatedSinceAsync("2026-10-26T12:00:00.0000000Z", 100)).ShouldAllBe(s => s.UserId == OwnerId);
    }

    [Fact]
    public async Task A_built_in_automation_round_trips_and_is_found_until_it_is_deleted()
    {
        var (conn, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = conn;
        var automations = new AutomationRepository(factory, new TestUserContext(OwnerId));
        await automations.InsertAsync(new Automation
        {
            Id = "dreaming-1", Name = "Dreaming", Prompt = "", TriggerType = "schedule", TriggerConfig = "0 3 * * *", TargetType = "dreaming",
            Builtin = "dreaming", IsEnabled = true, CreatedAt = "2026-10-27T03:00:00.0000000Z", UserId = OwnerId,
        });
        await automations.InsertAsync(new Automation
        {
            Id = "mine", Name = "Mine", Prompt = "Hi", TriggerType = "schedule", TriggerConfig = "0 9 * * 1", CreatedAt = "2026-10-27T03:00:00.0000000Z", UserId = OwnerId,
        });

        (await automations.GetByIdAsync("dreaming-1"))!.Builtin.ShouldBe("dreaming");
        (await automations.GetByIdAsync("mine"))!.Builtin.ShouldBeNull();
        (await automations.GetBuiltinAsync("dreaming"))!.Id.ShouldBe("dreaming-1");
        (await new AutomationRepository(factory, new TestUserContext("someone-else")).GetBuiltinAsync("dreaming")).ShouldBeNull();

        await automations.DeleteAsync("dreaming-1");

        (await automations.GetBuiltinAsync("dreaming")).ShouldBeNull();
    }

    [Fact]
    public async Task A_summary_is_written_with_its_settled_state_and_only_unsettled_nights_before_a_time_are_ended()
    {
        var (conn, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = conn;
        var runs = new AutomationRunRepository(factory, new TestUserContext(OwnerId));
        AutomationRun Night(string id, string startedAt, string? sessionId = null) => new()
        {
            Id = id, AutomationId = "dreaming-1", UserId = OwnerId, Trigger = "schedule", StartedAt = startedAt, Status = "started", SessionId = sessionId,
        };
        await runs.InsertAsync(Night("going", "2026-10-26T03:00:00.0000000Z"));
        await runs.InsertAsync(Night("settled", "2026-10-25T03:00:00.0000000Z"));
        await runs.InsertAsync(Night("recent", "2026-10-27T09:00:00.0000000Z"));
        await runs.InsertAsync(Night("with-session", "2026-10-24T03:00:00.0000000Z", sessionId: "s1"));
        await runs.SetSummaryAsync("going", "Dreaming about alpha (1 of 2)…");
        await runs.SetSummaryAsync("settled", "alpha: no changes · 1 min, 1k tokens", "done");
        await runs.SetSummaryAsync("recent", "Dreaming about beta (1 of 1)…");

        var ended = await runs.EndUnsettledNightsAsync("2026-10-27T08:00:00.0000000Z", "Fleet stopped during the night.");

        ended.ShouldBe(1);
        var all = (await runs.ListByAutomationAsync("dreaming-1", 10)).ToDictionary(r => r.Id);
        (all["going"].SettledState, all["going"].Error, all["going"].Summary).ShouldBe(("failed", "Fleet stopped during the night.", "Dreaming about alpha (1 of 2)…"));
        (all["settled"].SettledState, all["settled"].Summary).ShouldBe(("done", "alpha: no changes · 1 min, 1k tokens"));
        all["recent"].SettledState.ShouldBeNull();
        all["with-session"].SettledState.ShouldBeNull();
    }
}
