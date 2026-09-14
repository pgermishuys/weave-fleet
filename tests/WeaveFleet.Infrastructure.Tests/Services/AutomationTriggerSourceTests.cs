using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Tests.Services;

/// <summary>
/// Where automations get their timing and their events from: the scheduler's reading of a cron in the
/// automation's time zone, and what the outbox dispatcher tells the automation dispatcher about each event.
/// </summary>
public sealed class AutomationTriggerSourceTests
{
    private static readonly DateTime MondayMidnightUtc = new(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);

    // ── Schedules in the automation's time zone ─────────────────────────────────

    [Fact]
    public void Cron_is_read_in_the_automations_time_zone()
    {
        var next = NextRun("0 9 * * 1", "Africa/Johannesburg");

        // 09:00 in Johannesburg (UTC+2) is 07:00 UTC.
        next.ShouldBe(new DateTime(2026, 9, 14, 7, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Cron_follows_daylight_saving_in_the_time_zone()
    {
        // London is on BST (UTC+1) in September and GMT (UTC+0) from late October.
        NextRun("0 9 * * *", "Europe/London").ShouldBe(new DateTime(2026, 9, 14, 8, 0, 0, DateTimeKind.Utc));
        NextRun("0 9 * * *", "Europe/London", from: new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc))
            .ShouldBe(new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Cron_without_a_time_zone_is_read_in_utc()
    {
        NextRun("0 9 * * 1", timeZone: null).ShouldBe(new DateTime(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Cron_with_an_unknown_time_zone_falls_back_to_utc()
    {
        NextRun("0 9 * * 1", "Mars/Olympus_Mons").ShouldBe(new DateTime(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc));
    }

    // ── Which session an event is about ─────────────────────────────────────────

    [Fact]
    public void Delegation_events_are_about_the_parent_session_on_their_topic()
    {
        var payload = Json("""{"delegationId":"d1","parentSessionId":"parent-1","title":"Explore","status":"running"}""");

        InProcessOutboxDispatcher.ExtractSessionId("session:parent-1", payload).ShouldBe("parent-1");
    }

    [Fact]
    public void Delegation_events_fall_back_to_the_parent_session_in_the_payload()
    {
        var payload = Json("""{"delegationId":"d1","parentSessionId":"parent-1"}""");

        InProcessOutboxDispatcher.ExtractSessionId("delegations", payload).ShouldBe("parent-1");
    }

    [Fact]
    public void Session_lifecycle_events_carry_the_session_in_the_payload()
    {
        var payload = Json("""{"sessionId":"s1","title":"Fix the build"}""");

        InProcessOutboxDispatcher.ExtractSessionId("sessions", payload).ShouldBe("s1");
    }

    [Fact]
    public void Only_sub_agent_sessions_count_as_sub_agent_session_starts()
    {
        InProcessOutboxDispatcher.IsSubAgentSessionCreated("session_created", Json("""{"sessionId":"child","parentSessionId":"p"}""")).ShouldBeTrue();
        InProcessOutboxDispatcher.IsSubAgentSessionCreated("session_created", Json("""{"sessionId":"s1"}""")).ShouldBeFalse();
        InProcessOutboxDispatcher.IsSubAgentSessionCreated("delegation.created", Json("""{"parentSessionId":"p"}""")).ShouldBeFalse();
    }

    // ── The feedback-loop guard ─────────────────────────────────────────────────

    [Fact]
    public async Task A_sub_agent_of_an_automation_session_reports_the_automation()
    {
        var sessions = new FakeSessionRepository();
        sessions.Seed(new Session { Id = "run-1", SourceReference = "automation:auto-1" });
        sessions.Seed(new Session { Id = "child-1", ParentSessionId = "run-1" });
        sessions.Seed(new Session { Id = "grandchild-1", ParentSessionId = "child-1" });

        (await InProcessOutboxDispatcher.ResolveSourceReferenceAsync(sessions, "grandchild-1")).ShouldBe("automation:auto-1");
    }

    [Fact]
    public async Task A_session_a_person_started_reports_its_own_source()
    {
        var sessions = new FakeSessionRepository();
        sessions.Seed(new Session { Id = "s1", SourceReference = "github:owner/repo#12" });
        sessions.Seed(new Session { Id = "child-1", ParentSessionId = "s1" });

        (await InProcessOutboxDispatcher.ResolveSourceReferenceAsync(sessions, "s1")).ShouldBe("github:owner/repo#12");
        (await InProcessOutboxDispatcher.ResolveSourceReferenceAsync(sessions, "child-1")).ShouldBeNull();
    }

    [Fact]
    public async Task A_parent_cycle_stops_instead_of_looping()
    {
        var sessions = new FakeSessionRepository();
        sessions.Seed(new Session { Id = "a", ParentSessionId = "b" });
        sessions.Seed(new Session { Id = "b", ParentSessionId = "a" });

        (await InProcessOutboxDispatcher.ResolveSourceReferenceAsync(sessions, "a")).ShouldBeNull();
        sessions.GetByIdAsyncCalls.Count.ShouldBeLessThanOrEqualTo(10);
    }

    [Fact]
    public async Task No_session_means_no_source()
    {
        var sessions = new FakeSessionRepository();

        (await InProcessOutboxDispatcher.ResolveSourceReferenceAsync(sessions, null)).ShouldBeNull();
        (await InProcessOutboxDispatcher.ResolveSourceReferenceAsync(sessions, "missing")).ShouldBeNull();
    }

    // ── What the automation's prompt is told ────────────────────────────────────

    [Theory]
    [InlineData("session_created", """{"sessionId":"s1","title":"Fix the build"}""", "A session started: Fix the build")]
    [InlineData("session_archived", """{"sessionId":"s1","archivedAt":"2026-09-14T09:00:00Z"}""", "A session was archived")]
    [InlineData("session_deleted", """{"sessionId":"s1"}""", "A session was deleted")]
    [InlineData("delegation.created", """{"parentSessionId":"p","title":"Explore the repo"}""", "A sub agent started: Explore the repo")]
    [InlineData("delegation.updated", """{"parentSessionId":"p","title":"Explore the repo","status":"completed"}""", "A sub agent is completed: Explore the repo")]
    public void Event_summaries_describe_the_events_that_reach_automations(string eventType, string payload, string expected)
    {
        InProcessOutboxDispatcher.BuildEventSummary(eventType, Json(payload)).ShouldBe(expected);
    }

    private static DateTime? NextRun(string cron, string? timeZone, DateTime? from = null)
    {
        var scheduler = new AutomationSchedulerService(null!, NullLogger<AutomationSchedulerService>.Instance);
        var automation = new Automation { Id = "auto-1", TriggerType = "schedule", TriggerConfig = cron, TimeZone = timeZone };
        return scheduler.NextOccurrenceUtc(automation, from ?? MondayMidnightUtc);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
