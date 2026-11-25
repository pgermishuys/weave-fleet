using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Automations;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Tests.Machines;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>A started run whose session's last turn failed reads Failed with the provider's words, never Done.</summary>
public sealed class AutomationRunFailedTurnTests
{
    private const string Message = "429 Too Many Requests: rate limit exceeded for gpt-5, try again in 20 seconds";

    private readonly InMemoryAutomationRunRepository _runs = new();
    private readonly SessionActivityTracker _activity = new();
    private readonly Failures _failures = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero));
    private readonly AutomationRunService _sut;

    public AutomationRunFailedTurnTests()
    {
        _sut = new AutomationRunService(
            _runs, new Executor(), _activity, _time, NullLogger<AutomationRunService>.Instance, FakeMachine.Unused,
            interruptions: _failures);
    }

    private static Automation Automation() => new()
    {
        Id = "auto-1", Name = "Weekly digest", Prompt = "Summarise", TriggerType = "schedule", TriggerConfig = "0 9 * * 1",
        MaxConcurrentRuns = 1, TargetType = "new_session", UserId = "user-1",
    };

    [Fact]
    public async Task A_run_whose_turn_failed_reads_failed_with_the_providers_message()
    {
        var run = await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));
        _failures.Failed["session-1"] = new SessionFailure(_time.GetUtcNow(), Message);

        (await _sut.StateOfAsync(run)).ShouldBe(AutomationRunState.Failed);
        run.Error.ShouldBe(Message);

        var listed = await _sut.StatesOfAsync([run]);
        listed.ShouldBe([AutomationRunState.Failed]);
    }

    [Fact]
    public async Task A_later_good_turn_clears_it_and_a_busy_session_is_still_running()
    {
        var run = await _sut.RunAsync(Automation(), new AutomationRunTrigger("schedule"));

        (await _sut.StateOfAsync(run)).ShouldBe(AutomationRunState.Done);
        run.Error.ShouldBeNull();

        _failures.Failed["session-1"] = new SessionFailure(_time.GetUtcNow(), Message);
        _activity.Update("session-1", "busy", "user-1");
        (await _sut.StateOfAsync(run)).ShouldBe(AutomationRunState.Running);
    }

    private sealed class Executor : IAutomationExecutor
    {
        public Task<AutomationExecutionOutcome> ExecuteAsync(
            Automation automation, string? eventType = null, string? eventSummary = null, string? previousSessionId = null, CancellationToken ct = default)
            => Task.FromResult(new AutomationExecutionOutcome("session-1", "instance-1", null));
    }

    private sealed class Failures : ISessionInterruptionRepository
    {
        public Dictionary<string, SessionFailure> Failed { get; } = [];
        public Task<IReadOnlyDictionary<string, SessionFailure>> ListFailuresForUserAsync() => Task.FromResult<IReadOnlyDictionary<string, SessionFailure>>(Failed);
        public Task FlagTurnAsync(string sessionId, DateTimeOffset startedAt, bool waiting) => throw new NotSupportedException();
        public Task SetWaitingAsync(string sessionId, bool waiting) => throw new NotSupportedException();
        public Task ClearTurnAsync(string sessionId) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlaggedTurn>> ListFlaggedAsync() => throw new NotSupportedException();
        public Task SetFailureAsync(string sessionId, DateTimeOffset at, string message) => throw new NotSupportedException();
        public Task<bool> ClearFailureAsync(string sessionId) => throw new NotSupportedException();
        public Task<SessionInterruption?> GetAsync(string sessionId) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionInterruption>> ListForUserAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionInterruption>> ListAllAsync() => throw new NotSupportedException();
        public Task SaveAsync(SessionInterruption interruption) => throw new NotSupportedException();
        public Task<SessionInterruption?> RemoveAsync(string sessionId) => throw new NotSupportedException();
    }
}
