using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Tests.Services;

public sealed class AutomationScheduleTests
{
    // Monday 14 September 2026.
    private static readonly DateTime Monday = new(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);

    private static Automation Cron(string cron, string? timeZone = null) =>
        new() { Id = "auto-1", Name = "Weekly digest", TriggerType = "schedule", TriggerConfig = cron, TimeZone = timeZone };

    private static Automation Once(string at, string? timeZone = null) =>
        new() { Id = "auto-1", Name = "Release notes check", TriggerType = "once", TriggerConfig = at, TimeZone = timeZone };

    [Fact]
    public void A_run_that_is_due_now_is_on_time()
    {
        var decision = AutomationSchedule.Decide(Cron("0 9 * * 1"), Monday.AddHours(8), Monday.AddHours(9).AddSeconds(20));

        decision.ShouldBe(new ScheduleDecision(ScheduleDecisionKind.OnTime, Monday.AddHours(9)));
    }

    [Fact]
    public void A_run_missed_by_less_than_three_hours_is_caught_up()
    {
        var decision = AutomationSchedule.Decide(Cron("0 9 * * 1"), Monday.AddHours(8), Monday.AddHours(10).AddMinutes(30));

        decision.ShouldBe(new ScheduleDecision(ScheduleDecisionKind.CatchUp, Monday.AddHours(9)));
    }

    [Fact]
    public void A_run_missed_by_more_than_three_hours_is_skipped()
    {
        var decision = AutomationSchedule.Decide(Cron("0 9 * * 1"), Monday.AddHours(8), Monday.AddHours(12).AddMinutes(1));

        decision.ShouldBe(new ScheduleDecision(ScheduleDecisionKind.Missed, Monday.AddHours(9)));
    }

    [Fact]
    public void An_occurrence_already_handled_is_not_due_again()
    {
        AutomationSchedule.Decide(Cron("0 9 * * 1"), Monday.AddHours(9), Monday.AddHours(9).AddSeconds(40)).ShouldBeNull();
    }

    [Fact]
    public void After_a_long_gap_only_the_latest_occurrence_counts()
    {
        var decision = AutomationSchedule.Decide(Cron("* * * * *"), Monday.AddHours(8), Monday.AddHours(8).AddMinutes(10).AddSeconds(5));

        decision.ShouldBe(new ScheduleDecision(ScheduleDecisionKind.OnTime, Monday.AddHours(8).AddMinutes(10)));
    }

    [Fact]
    public void A_cron_is_read_on_the_automations_clock()
    {
        // 09:00 in Johannesburg (UTC+2) is 07:00 UTC.
        var decision = AutomationSchedule.Decide(Cron("0 9 * * *", "Africa/Johannesburg"), Monday.AddHours(6), Monday.AddHours(7).AddSeconds(10));

        decision.ShouldBe(new ScheduleDecision(ScheduleDecisionKind.OnTime, Monday.AddHours(7)));
    }

    [Fact]
    public void A_one_off_time_is_read_on_the_automations_clock()
    {
        AutomationSchedule.OnceUtc("2026-09-21T09:00", "Africa/Johannesburg")
            .ShouldBe(new DateTime(2026, 9, 21, 7, 0, 0, DateTimeKind.Utc));
        AutomationSchedule.OnceUtc("2026-09-21T09:00", null)
            .ShouldBe(new DateTime(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc));
        AutomationSchedule.OnceUtc("next monday", null).ShouldBeNull();
    }

    [Fact]
    public void A_one_off_is_due_once_and_then_never_again()
    {
        var once = Once("2026-09-14T09:00");

        AutomationSchedule.NextOccurrenceUtc(once, Monday).ShouldBe(Monday.AddHours(9));
        AutomationSchedule.Decide(once, Monday, Monday.AddHours(9).AddSeconds(5))!.Kind.ShouldBe(ScheduleDecisionKind.OnTime);
        AutomationSchedule.NextOccurrenceUtc(once, Monday.AddHours(10)).ShouldBeNull();
        AutomationSchedule.Decide(once, Monday.AddHours(9), Monday.AddHours(10)).ShouldBeNull();
    }

    [Fact]
    public void Event_automations_have_no_occurrences()
    {
        var automation = new Automation { TriggerType = "event", TriggerConfig = """{"eventType":"session_created"}""" };

        AutomationSchedule.NextOccurrenceUtc(automation, Monday).ShouldBeNull();
        AutomationSchedule.Decide(automation, Monday, Monday.AddDays(1)).ShouldBeNull();
    }

    [Fact]
    public void A_skipped_run_says_when_it_was_on_the_automations_clock()
    {
        AutomationSchedule.DescribeMissed(Cron("0 9 * * 1", "Africa/Johannesburg"), Monday.AddHours(7))
            .ShouldBe("Skipped: Fleet wasn't running at Mon 14 Sep, 09:00, and it was more than 3 hours late when Fleet started.");
    }

    [Theory]
    [InlineData("schedule", "0 9 * * 1", null, null)]
    [InlineData("schedule", "not a cron", null, "Invalid cron expression")]
    [InlineData("schedule", "0 9 * * 1", "Nowhere/Special", "Unknown time zone")]
    [InlineData("once", "2026-09-21T09:00", "Europe/London", null)]
    [InlineData("once", "Monday at 9", null, "needs a date and time")]
    [InlineData("once", "2026-09-13T09:00", null, "already passed")]
    [InlineData("event", "anything", null, null)]
    public void Validate_checks_timed_triggers(string triggerType, string config, string? timeZone, string? expectedError)
    {
        var error = AutomationSchedule.Validate(triggerType, config, timeZone, Monday, requireFuture: true);

        if (expectedError is null)
            error.ShouldBeNull();
        else
            error.ShouldNotBeNull().Description.ShouldContain(expectedError);
    }

    [Fact]
    public void A_past_one_off_time_is_fine_when_it_need_not_be_in_the_future()
    {
        AutomationSchedule.Validate("once", "2026-09-13T09:00", null, Monday, requireFuture: false).ShouldBeNull();
    }
}
