using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Automations;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Tests.Machines;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>A built-in automation (Dreaming) is Fleet's: people can't edit or delete it, it hides with its feature, and it runs through its host.</summary>
public sealed class BuiltinAutomationServiceTests
{
    private readonly InMemoryAutomationRepository _repository = new();
    private readonly FakeBuiltinAutomations _builtins = new();
    private readonly InMemoryAutomationRunRepository _runs = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 3, 0, 0, TimeSpan.Zero));
    private readonly AutomationService _service;
    private readonly AutomationRunService _runService;
    private readonly Automation _dreaming = new()
    {
        Id = "dreaming-1", Name = "Dreaming", TriggerType = "schedule", TriggerConfig = "0 3 * * *", TargetType = "dreaming",
        Builtin = BuiltinAutomations.Dreaming, IsEnabled = true, MaxConcurrentRuns = 1, UserId = "user-1",
    };

    public BuiltinAutomationServiceTests()
    {
        _repository.Seed(_dreaming);
        _service = new AutomationService(_repository, new TestUserContext(), builtins: _builtins);
        _runService = new AutomationRunService(
            _runs, new ThrowingExecutor(), new SessionActivityTracker(), _time, NullLogger<AutomationRunService>.Instance, FakeMachine.Unused, builtins: _builtins);
    }

    [Fact]
    public async Task A_built_in_automation_cant_be_deleted()
    {
        var result = await _service.DeleteAsync(_dreaming.Id);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe(AutomationService.BuiltinRefusal);
        (await _repository.GetBuiltinAsync(BuiltinAutomations.Dreaming)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_built_in_automation_cant_be_edited()
    {
        var result = await _service.UpdateAsync(_dreaming.Id, "Mine", "Do something else", "schedule", "0 4 * * *", 1, 10, 30);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe(AutomationService.BuiltinRefusal);
        (await _repository.GetByIdAsync(_dreaming.Id))!.TriggerConfig.ShouldBe("0 3 * * *");
    }

    [Fact]
    public async Task A_built_in_automation_is_listed_only_while_its_feature_is_on()
    {
        _repository.Seed(new Automation { Id = "mine", Name = "Mine", TriggerType = "schedule", TriggerConfig = "0 9 * * 1", UserId = "user-1" });

        _builtins.On = false;
        (await _service.ListAsync()).Value.Select(a => a.Id).ShouldBe(["mine"]);

        _builtins.On = true;
        (await _service.ListAsync()).Value.Select(a => a.Id).ShouldBe(["dreaming-1", "mine"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_built_in_run_goes_through_its_host_and_runs_until_the_night_settles()
    {
        var run = await _runService.RunAsync(_dreaming, new AutomationRunTrigger("schedule"));

        _builtins.RunIds.ShouldBe([run.Id]);
        run.Status.ShouldBe(AutomationRunStatus.Started);
        run.Summary.ShouldBe("Reading alpha…");
        // The host writes the summary to the row itself, as DreamingAutomation does before it returns.
        await _runs.SetSummaryAsync(run.Id, run.Summary!);
        var stored = _runs.All.Single();
        _runService.StateOf(stored).ShouldBe(AutomationRunState.Running);

        await _runs.SetSummaryAsync(stored.Id, "alpha: no changes · 1 min, 1k tokens", "done");

        _runService.StateOf(_runs.All.Single()).ShouldBe(AutomationRunState.Done);
    }

    [Fact]
    public async Task A_built_in_run_the_host_skips_is_recorded_as_skipped()
    {
        _builtins.Outcome = AutomationExecutionOutcome.SkippedBecause("Skipped: no new sessions since the last night, nothing to read.");

        var run = await _runService.RunAsync(_dreaming, new AutomationRunTrigger("schedule"));

        run.Status.ShouldBe(AutomationRunStatus.Skipped);
        run.Summary.ShouldBeNull();
    }

    private sealed class ThrowingExecutor : IAutomationExecutor
    {
        public Task<AutomationExecutionOutcome> ExecuteAsync(
            Automation automation, string? eventType = null, string? eventSummary = null, string? previousSessionId = null, CancellationToken ct = default)
            => throw new InvalidOperationException("A built-in automation must not go through the executor.");
    }
}
