using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// Stopping one piece of running work and reading its output: both go to the session's live harness, and only for work
/// the harness said it can do that for.
/// </summary>
public sealed class SessionOrchestratorWorkTests : IAsyncDisposable
{
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly FakeHarnessSession _harness = new("inst-1");

    public SessionOrchestratorWorkTests()
    {
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1",
            WorkspaceId = "ws-1",
            InstanceId = "inst-1",
            Title = "Run the suite",
            Status = "active",
            Directory = "/tmp/repo",
            CreatedAt = "2026-01-01",
            HarnessType = "opencode2",
            UserId = "user-1",
        });
        _builder.InstanceTracker.Register("inst-1", _harness);
    }

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private Delegation Seed(string workId, bool canStop = true, bool canReadOutput = true, string status = "running")
    {
        var work = new Delegation
        {
            Id = $"work-{workId}",
            ParentSessionId = "s1",
            ParentToolCallId = $"call-{workId}",
            Title = "shell",
            Label = "bun run test:e2e",
            Status = status,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O"),
            Kind = WorkKinds.Shell,
            WorkId = workId,
            Background = true,
            CanStop = canStop,
            CanReadOutput = canReadOutput,
        };
        _builder.DelegationRepository.Seed(work);
        return work;
    }

    [Fact]
    public async Task Stop_asks_the_harness_and_the_work_ends_cancelled_at_once()
    {
        var work = Seed("sh_1");
        _harness.StopWorkBehavior = _ => true;

        var result = await _builder.Build().StopWorkAsync("s1", work.Id);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        _harness.StopWorkCalls.ShouldBe(["sh_1"]);
        result.Value.Status.ShouldBe("cancelled");
        result.Value.EndedReason.ShouldBe(WorkEndedReasons.Cancelled);
        _builder.EventBroadcaster.Broadcasts.ShouldContain(b => b.Type == EventTypes.WorkEnded && b.Topic == "session:s1");
    }

    [Fact]
    public async Task Work_the_harness_no_longer_has_ends_lost()
    {
        var work = Seed("sh_gone");
        _harness.StopWorkBehavior = _ => false;

        var result = await _builder.Build().StopWorkAsync("s1", work.Id);

        result.Value.EndedReason.ShouldBe(WorkEndedReasons.Lost);
    }

    [Fact]
    public async Task Work_the_harness_cant_stop_is_refused_without_asking_it()
    {
        var work = Seed("sh_1", canStop: false);
        _harness.StopWorkBehavior = _ => true;

        var result = await _builder.Build().StopWorkAsync("s1", work.Id);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldStartWith("Validation.");
        _harness.StopWorkCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Work_that_finished_is_a_conflict()
    {
        var work = Seed("sh_1", status: "completed");
        _harness.StopWorkBehavior = _ => true;

        var result = await _builder.Build().StopWorkAsync("s1", work.Id);

        result.Error.Code.ShouldBe("General.Conflict");
        _harness.StopWorkCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Another_sessions_work_is_not_found()
    {
        _builder.DelegationRepository.Seed(new Delegation { Id = "other", ParentSessionId = "s2", WorkId = "sh_x", Status = "running", CanStop = true, Kind = WorkKinds.Shell });

        var result = await _builder.Build().StopWorkAsync("s1", "other");

        result.Error.Code.ShouldEndWith(".NotFound");
    }

    [Fact]
    public async Task A_session_that_isnt_running_has_no_work_to_stop()
    {
        var work = Seed("sh_1");
        _builder.InstanceTracker.Remove("inst-1");

        var result = await _builder.Build().StopWorkAsync("s1", work.Id);

        result.Error.Code.ShouldBe("General.Conflict");
    }

    [Fact]
    public async Task Output_is_read_from_the_harness_from_the_offset_asked_for()
    {
        var work = Seed("sh_1");
        _harness.WorkOutputBehavior = (id, offset) => id == "sh_1" ? new WorkOutput("late-output\n", offset + 12, 40, false) : null;

        var result = await _builder.Build().ReadWorkOutputAsync("s1", work.Id, 28);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        result.Value.ShouldBe(new WorkOutput("late-output\n", 40, 40, false));
    }

    [Fact]
    public async Task Output_of_work_without_any_is_refused()
    {
        var work = Seed("call_sub", canReadOutput: false);

        var result = await _builder.Build().ReadWorkOutputAsync("s1", work.Id, 0);

        result.Error.Code.ShouldStartWith("Validation.");
    }
}
