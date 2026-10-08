using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
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
            remoteRuns: _atlas.Runs);
    }

    public void Dispose() => _atlas.Dispose();

    private static Automation Automation(string targetType = "new_session", string? machine = FakeMachine.Id) => new()
    {
        Id = "auto-1", Name = "Nightly check", Prompt = "Check the build", TriggerType = "schedule", TriggerConfig = "0 2 * * *",
        MaxConcurrentRuns = 1, TargetType = targetType, TargetMachineId = machine, UserId = "user-1",
    };

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

    [Theory]
    [InlineData("busy", AutomationRunState.Running)]
    [InlineData("idle", AutomationRunState.Done)]
    public async Task A_runs_state_comes_from_its_session_on_the_machine(string activity, string state)
    {
        _executor.Outcome = StartedOnAtlas();
        _atlas.Answer = request => request == "GET /api/sessions/atlas-session"
            ? (HttpStatusCode.OK, $$"""{"id":"atlas-session","activityStatus":"{{activity}}"}""")
            : null;

        var run = await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));

        (await _sut.StateOfAsync(run)).ShouldBe(state);
    }

    [Fact]
    public async Task A_run_on_a_machine_that_doesnt_answer_reads_done()
    {
        _executor.Outcome = StartedOnAtlas();
        var run = await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));
        _atlas.Away = true;

        (await _sut.StateOfAsync(run)).ShouldBe(AutomationRunState.Done);
    }

    [Fact]
    public async Task A_machine_the_watcher_found_away_isnt_asked()
    {
        _executor.Outcome = StartedOnAtlas();
        var run = await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));
        await _atlas.Machines.UpdateStatusAsync(FakeMachine.Id, RemoteMachineStatuses.Unreachable, null);

        (await _sut.StateOfAsync(run)).ShouldBe(AutomationRunState.Done);
        _atlas.Requests.ShouldBeEmpty();
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
        await _sut.RunAsync(automation, new AutomationRunTrigger("schedule"));
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
