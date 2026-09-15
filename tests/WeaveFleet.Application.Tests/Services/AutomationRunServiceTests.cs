using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class AutomationRunServiceTests
{
    private readonly InMemoryAutomationRunRepository _runs = new();
    private readonly RecordingExecutor _executor = new();
    private readonly SessionActivityTracker _activity = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero));
    private readonly AutomationRunService _sut;

    public AutomationRunServiceTests()
    {
        _sut = new AutomationRunService(_runs, _executor, _activity, _time, NullLogger<AutomationRunService>.Instance);
    }

    private static Automation Automation(int maxConcurrentRuns = 1, string targetType = "new_session") => new()
    {
        Id = "auto-1",
        Name = "Weekly digest",
        Prompt = "Summarise the open PRs",
        TriggerType = "schedule",
        TriggerConfig = "0 9 * * 1",
        MaxConcurrentRuns = maxConcurrentRuns,
        TargetType = targetType,
        UserId = "user-1",
    };

    [Fact]
    public async Task A_run_is_recorded_with_the_session_it_started()
    {
        var scheduledFor = new DateTime(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc);

        var run = await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule", scheduledFor));

        run.Status.ShouldBe(AutomationRunStatus.Started);
        var stored = _runs.All.ShouldHaveSingleItem();
        (stored.Trigger, stored.Status, stored.SessionId, stored.UserId).ShouldBe(("schedule", "started", "session-1", "user-1"));
        stored.ScheduledFor.ShouldBe(scheduledFor.ToString("O"));
        _sut.StateOf(stored).ShouldBe(AutomationRunState.Done);
    }

    [Fact]
    public async Task A_run_that_couldnt_start_says_why()
    {
        _executor.Outcome = AutomationExecutionOutcome.Failed("Couldn't start: ~/source/t3code isn't inside a workspace root.");

        await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));

        var stored = _runs.All.ShouldHaveSingleItem();
        stored.Status.ShouldBe(AutomationRunStatus.Failed);
        stored.Error.ShouldBe("Couldn't start: ~/source/t3code isn't inside a workspace root.");
        _sut.StateOf(stored).ShouldBe(AutomationRunState.Failed);
    }

    [Fact]
    public async Task A_run_is_skipped_while_the_last_one_is_still_going()
    {
        await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));
        _activity.Update("session-1", "busy", "user-1");

        var second = await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));

        second.Status.ShouldBe(AutomationRunStatus.Skipped);
        second.Error.ShouldBe("Skipped: the last run was still going.");
        _executor.Calls.Count.ShouldBe(1);
        _sut.StateOf(_runs.All[0]).ShouldBe(AutomationRunState.Running);
    }

    [Fact]
    public async Task A_run_goes_ahead_once_the_last_one_has_finished()
    {
        await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));
        _activity.Update("session-1", "idle", "user-1");

        (await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"))).Status.ShouldBe(AutomationRunStatus.Started);
    }

    [Fact]
    public async Task Without_skipping_runs_can_overlap()
    {
        await _sut.RunAsync(Automation(maxConcurrentRuns: 0), new AutomationRunTrigger("schedule"));
        _activity.Update("session-1", "busy", "user-1");

        (await _sut.RunAsync(Automation(maxConcurrentRuns: 0), new AutomationRunTrigger("schedule"))).Status.ShouldBe(AutomationRunStatus.Started);
    }

    [Fact]
    public async Task Run_now_is_never_skipped()
    {
        await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));
        _activity.Update("session-1", "busy", "user-1");

        var run = await _sut.RunAsync(Automation(), new AutomationRunTrigger(AutomationRunTrigger.Manual));

        run.Status.ShouldBe(AutomationRunStatus.Started);
        _executor.Calls[^1].EventType.ShouldBe("manual");
        // The agent gets the prompt as a scheduled run would, with no "[Context] manual: …" in front.
        _executor.Calls[^1].EventSummary.ShouldBeNull();
    }

    [Fact]
    public async Task Begin_records_the_run_as_starting_before_the_session_exists()
    {
        var run = await _sut.BeginAsync(Automation(), new AutomationRunTrigger(AutomationRunTrigger.Manual));

        _sut.StateOf(_runs.All.ShouldHaveSingleItem()).ShouldBe(AutomationRunState.Starting);
        _executor.Calls.ShouldBeEmpty();

        await _sut.FinishAsync(Automation(), run, new AutomationRunTrigger(AutomationRunTrigger.Manual));
        _runs.All.ShouldHaveSingleItem().Status.ShouldBe(AutomationRunStatus.Started);
    }

    [Fact]
    public async Task The_same_session_target_continues_the_last_runs_session()
    {
        var automation = Automation(maxConcurrentRuns: 0, targetType: "same_session");
        await _sut.RunAsync(automation, new AutomationRunTrigger("schedule"));
        _executor.Outcome = new AutomationExecutionOutcome("session-1", "instance-1", null);

        await _sut.RunAsync(automation, new AutomationRunTrigger("schedule"));

        _executor.Calls[0].PreviousSessionId.ShouldBeNull();
        _executor.Calls[1].PreviousSessionId.ShouldBe("session-1");
    }

    private sealed class RecordingExecutor : IAutomationExecutor
    {
        public List<(string AutomationId, string? EventType, string? PreviousSessionId, string? EventSummary)> Calls { get; } = [];
        public AutomationExecutionOutcome Outcome { get; set; } = new("session-1", "instance-1", null);

        public Task<AutomationExecutionOutcome> ExecuteAsync(
            Automation automation,
            string? eventType = null,
            string? eventSummary = null,
            string? previousSessionId = null,
            CancellationToken ct = default)
        {
            Calls.Add((automation.Id, eventType, previousSessionId, eventSummary));
            return Task.FromResult(Outcome);
        }
    }
}
