using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Automations;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Machines;

/// <summary>
/// A run that went to another machine keeps the machine, and how it's going comes from that machine: its session's
/// activity, or its workflow run's status.
/// </summary>
public sealed class RemoteAutomationRunStateTests : IDisposable
{
    private readonly FakeMachine _atlas = new();
    private readonly InMemoryAutomationRunRepository _runs = new();
    private readonly StubExecutor _executor = new();
    private readonly AutomationRunService _sut;

    public RemoteAutomationRunStateTests()
    {
        _sut = new AutomationRunService(
            _runs, _executor, new SessionActivityTracker(), _atlas.Time, NullLogger<AutomationRunService>.Instance,
            _atlas.Runs);
    }

    public void Dispose() => _atlas.Dispose();

    private static Automation Automation(string targetType = "new_session", string? machine = FakeMachine.Id) => new()
    {
        Id = "auto-1", Name = "Nightly check", Prompt = "Check the build", TriggerType = "schedule", TriggerConfig = "0 2 * * *",
        MaxConcurrentRuns = 1, TargetType = targetType, TargetMachineId = machine, UserId = "user-1",
    };

    /// <summary>The newest run, as it's stored.</summary>
    private AutomationRun Stored() => _runs.All.OrderByDescending(r => r.StartedAt, StringComparer.Ordinal).First();

    private static AutomationExecutionOutcome StartedOnAtlas(string sessionId = "atlas-session", string? workflowRunId = null) =>
        new(sessionId, null, null, workflowRunId, MachineId: FakeMachine.Id, MachineName: FakeMachine.Name);

    [Fact]
    public async Task A_run_keeps_the_machine_it_went_to()
    {
        _executor.Outcome = StartedOnAtlas();

        await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));

        var stored = _runs.All.ShouldHaveSingleItem();
        (stored.Status, stored.SessionId, stored.MachineId, stored.MachineName)
            .ShouldBe((AutomationRunStatus.Started, "atlas-session", FakeMachine.Id, FakeMachine.Name));
    }

    [Fact]
    public async Task A_run_here_keeps_no_machine()
    {
        _executor.Outcome = new AutomationExecutionOutcome("session-1", "instance-1", null);

        await _sut.RunAsync(Automation(machine: null), new AutomationRunTrigger("schedule"));

        var stored = _runs.All.ShouldHaveSingleItem();
        (stored.MachineId, stored.MachineName).ShouldBe((null, null));
        _atlas.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_run_whose_session_works_there_is_running_and_is_asked_again()
    {
        _executor.Outcome = StartedOnAtlas();
        _atlas.Answer = request => request == "GET /api/sessions/atlas-session"
            ? (HttpStatusCode.OK, """{"id":"atlas-session","activityStatus":"busy"}""")
            : null;
        var run = await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));

        (await _sut.StateOfAsync(run)).ShouldBe(AutomationRunState.Running);
        (await _sut.StateOfAsync(Stored())).ShouldBe(AutomationRunState.Running);

        Stored().SettledState.ShouldBeNull();
        _atlas.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_run_whose_session_is_idle_there_is_done_for_good_and_isnt_asked_again()
    {
        _executor.Outcome = StartedOnAtlas();
        _atlas.Answer = _ => (HttpStatusCode.OK, """{"id":"atlas-session","activityStatus":"idle"}""");
        var run = await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));

        (await _sut.StateOfAsync(run)).ShouldBe(AutomationRunState.Done);
        Stored().SettledState.ShouldBe(AutomationRunState.Done);

        _atlas.Away = true;
        (await _sut.StateOfAsync(Stored())).ShouldBe(AutomationRunState.Done);
        _atlas.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_run_whose_session_the_machine_no_longer_has_is_done()
    {
        _executor.Outcome = StartedOnAtlas();
        var run = await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));

        (await _sut.StateOfAsync(run)).ShouldBe(AutomationRunState.Done);
        Stored().SettledState.ShouldBe(AutomationRunState.Done);
    }

    [Fact]
    public async Task A_run_on_a_machine_that_doesnt_answer_says_so_and_isnt_settled()
    {
        _executor.Outcome = StartedOnAtlas();
        var run = await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));
        _atlas.Away = true;

        (await _sut.StateOfAsync(run)).ShouldBe(AutomationRunState.Unanswered);
        Stored().SettledState.ShouldBeNull();

        _atlas.Away = false;
        _atlas.Answer = _ => (HttpStatusCode.OK, """{"activityStatus":"busy"}""");
        (await _sut.StateOfAsync(Stored())).ShouldBe(AutomationRunState.Running);
    }

    [Fact]
    public async Task A_machine_the_watcher_found_away_isnt_asked_and_reads_as_not_answering()
    {
        _executor.Outcome = StartedOnAtlas();
        var run = await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));
        await _atlas.Machines.UpdateStatusAsync(FakeMachine.Id, RemoteMachineStatuses.Unreachable, null);

        (await _sut.StateOfAsync(run)).ShouldBe(AutomationRunState.Unanswered);
        _atlas.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_list_asks_the_machine_about_its_unfinished_runs_at_once_and_keeps_its_order()
    {
        await _runs.InsertAsync(new AutomationRun { Id = "here", AutomationId = "auto-1", UserId = "user-1", Trigger = "schedule", StartedAt = "2026-10-08T01:00:00Z", Status = AutomationRunStatus.Started, SessionId = "session-here" });
        await _runs.InsertAsync(new AutomationRun { Id = "a", AutomationId = "auto-1", UserId = "user-1", Trigger = "schedule", StartedAt = "2026-10-08T02:00:00Z", Status = AutomationRunStatus.Started, SessionId = "s-a", MachineId = FakeMachine.Id });
        await _runs.InsertAsync(new AutomationRun { Id = "b", AutomationId = "auto-1", UserId = "user-1", Trigger = "schedule", StartedAt = "2026-10-08T03:00:00Z", Status = AutomationRunStatus.Started, SessionId = "s-b", MachineId = FakeMachine.Id });
        await _runs.InsertAsync(new AutomationRun { Id = "c", AutomationId = "auto-1", UserId = "user-1", Trigger = "schedule", StartedAt = "2026-10-08T04:00:00Z", Status = AutomationRunStatus.Started, SessionId = "s-c", MachineId = FakeMachine.Id, SettledState = AutomationRunState.Ended });
        // Neither request is answered until both have arrived: asked one after the other, the first would never be.
        var arrived = 0;
        var both = new TaskCompletionSource();
        _atlas.BeforeAnswer = async _ =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
                both.SetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };
        _atlas.Answer = request => (HttpStatusCode.OK, request.EndsWith("s-a", StringComparison.Ordinal) ? """{"activityStatus":"busy"}""" : """{"activityStatus":"idle"}""");

        var runs = await _runs.ListByAutomationAsync("auto-1", 10);
        var states = await _sut.StatesOfAsync(runs);

        runs.Select(r => r.Id).ShouldBe(["c", "b", "a", "here"]);
        states.ShouldBe([AutomationRunState.Ended, AutomationRunState.Done, AutomationRunState.Running, AutomationRunState.Done]);
        _atlas.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_run_that_doesnt_answer_doesnt_hold_up_the_next_one()
    {
        _executor.Outcome = StartedOnAtlas();
        await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));
        _atlas.Away = true;

        var next = await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));

        next.Status.ShouldNotBe(AutomationRunStatus.Skipped);
        _executor.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task The_next_run_is_skipped_while_the_last_one_works_on_the_machine()
    {
        _executor.Outcome = StartedOnAtlas();
        _atlas.Answer = _ => (HttpStatusCode.OK, """{"activityStatus":"busy"}""");
        await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));

        var next = await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));

        next.Status.ShouldBe(AutomationRunStatus.Skipped);
        next.Error.ShouldBe("Skipped: the last run was still going.");
        _executor.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_workflow_runs_state_follows_its_run_on_the_machine_and_holds_up_the_next()
    {
        _executor.Outcome = StartedOnAtlas("step-1", "wf-run-1");
        _atlas.Answer = request => request == "GET /api/workflows/runs/wf-run-1"
            ? (HttpStatusCode.OK, """{"id":"wf-run-1","status":"waiting","waiting":{"stepTitle":"Approve the plan"}}""")
            : null;
        var first = await _sut.RunAsync(Automation("workflow"), new AutomationRunTrigger("schedule"));

        (await _sut.StateOfAsync(first)).ShouldBe(AutomationRunState.Waiting);
        var next = await _sut.RunAsync(Automation("workflow"), new AutomationRunTrigger("schedule"));
        next.Error.ShouldBe("Skipped: the last run is still waiting on you (Approve the plan).");
        _runs.All.Single(r => r.Id == first.Id).SettledState.ShouldBeNull();
    }

    [Fact]
    public async Task A_workflow_run_that_ended_there_is_settled_and_doesnt_hold_up_the_next()
    {
        _executor.Outcome = StartedOnAtlas("step-1", "wf-run-1");
        _atlas.Answer = request => request == "GET /api/workflows/runs/wf-run-1"
            ? (HttpStatusCode.OK, """{"id":"wf-run-1","status":"ended"}""")
            : null;
        var first = await _sut.RunAsync(Automation("workflow"), new AutomationRunTrigger("schedule"));

        (await _sut.StateOfAsync(first)).ShouldBe(AutomationRunState.Ended);
        _runs.All.Single(r => r.Id == first.Id).SettledState.ShouldBe(AutomationRunState.Ended);

        var next = await _sut.RunAsync(Automation("workflow"), new AutomationRunTrigger("schedule"));
        next.Status.ShouldBe(AutomationRunStatus.Started);
    }

    [Fact]
    public async Task The_same_session_each_run_only_goes_back_to_a_session_on_the_machine_it_runs_on_now()
    {
        _executor.Outcome = new AutomationExecutionOutcome("session-here", "instance-1", null);
        var automation = Automation("same_session", machine: null);
        automation.MaxConcurrentRuns = 0;
        await _sut.RunAsync(automation, new AutomationRunTrigger("schedule"));

        automation.TargetMachineId = FakeMachine.Id;
        _executor.Outcome = StartedOnAtlas();
        _atlas.Time.Advance(TimeSpan.FromDays(1));
        await _sut.RunAsync(automation, new AutomationRunTrigger("schedule"));
        _atlas.Time.Advance(TimeSpan.FromDays(1));
        await _sut.RunAsync(automation, new AutomationRunTrigger("schedule"));

        _executor.PreviousSessionIds.ShouldBe([null, null, "atlas-session"]);
    }

    private sealed class StubExecutor : IAutomationExecutor
    {
        public AutomationExecutionOutcome Outcome { get; set; } = new("session-1", "instance-1", null);
        public int Calls => PreviousSessionIds.Count;
        public List<string?> PreviousSessionIds { get; } = [];

        public Task<AutomationExecutionOutcome> ExecuteAsync(
            Automation automation,
            string? eventType = null,
            string? eventSummary = null,
            string? previousSessionId = null,
            CancellationToken ct = default)
        {
            PreviousSessionIds.Add(previousSessionId);
            return Task.FromResult(Outcome);
        }
    }
}
