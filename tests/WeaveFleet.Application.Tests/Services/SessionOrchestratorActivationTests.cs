using Shouldly;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// Waking a session whose harness isn't running (<see cref="SessionOrchestrator.ActivateSessionAsync(string, CancellationToken)"/>),
/// and telling its harness the permission level, on wake and before every prompt.
/// </summary>
public sealed class SessionOrchestratorActivationTests : IAsyncDisposable
{
    private const string SessionId = "sess-wake";
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly FakeHarnessRuntime _runtime;
    private readonly FakeHarnessSession _woken = new("inst-woken") { ResumeToken = "token-fresh" };

    public SessionOrchestratorActivationTests()
    {
        _runtime = _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true });
        _runtime.DefaultSession = _woken;
        _builder.WorkspaceRepository.Seed(new Workspace
        {
            Id = "ws-wake", Directory = "/tmp/fleet-wake", IsolationStrategy = "existing", CreatedAt = "2026-01-01", UserId = "user-1",
        });
    }

    public ValueTask DisposeAsync() => _woken.DisposeAsync();

    private void SeedSession(string? resumeToken, string workspaceId = "ws-wake") => _builder.SessionRepository.Seed(new Session
    {
        Id = SessionId,
        WorkspaceId = workspaceId,
        InstanceId = "inst-asleep",
        Title = "Rate limit headers",
        Status = "stopped",
        Directory = "/tmp/fleet-wake",
        CreatedAt = "2026-01-01",
        RetentionStatus = "active",
        HarnessType = "opencode",
        RuntimeMode = "manual",
        HarnessResumeToken = resumeToken,
        UserId = "user-1",
    });

    [Fact]
    public async Task a_session_that_was_never_prompted_wakes_on_a_fresh_harness_session_and_keeps_its_token()
    {
        SeedSession(resumeToken: null);
        var sut = _builder.Build();

        var result = await sut.ActivateSessionAsync(SessionId);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeSameAs(_woken);
        _runtime.SpawnCalls.ShouldHaveSingleItem().SessionId.ShouldBe(SessionId);
        _runtime.ResumeCalls.ShouldBeEmpty();
        var stored = await _builder.SessionRepository.GetByIdAsync(SessionId);
        stored!.InstanceId.ShouldBe("inst-woken");
        stored.HarnessResumeToken.ShouldBe("token-fresh");
        _builder.InstanceTracker.Get("inst-woken").ShouldBeSameAs(_woken);
    }

    [Fact]
    public async Task a_prompted_session_resumes_with_its_token_and_says_it_is_running_and_idle()
    {
        SeedSession(resumeToken: "token-saved");
        var sut = _builder.Build();

        var result = await sut.ActivateSessionAsync(SessionId);

        result.IsSuccess.ShouldBeTrue();
        _runtime.SpawnCalls.ShouldBeEmpty();
        var resume = _runtime.ResumeCalls.ShouldHaveSingleItem();
        resume.ResumeToken.ShouldBe("token-saved");
        resume.WorkingDirectory.ShouldBe("/tmp/fleet-wake");
        resume.DelegatedChild.ShouldBeFalse();
        (await _builder.SessionRepository.GetByIdAsync(SessionId))!.HarnessResumeToken.ShouldBe("token-saved");

        var status = _builder.EventBroadcaster.Broadcasts.Where(b => b.Topic == $"session:{SessionId}" && b.Type == "session.status").ShouldHaveSingleItem();
        status.Payload.GetProperty("lifecycleStatus").GetString().ShouldBe("running");
        status.Payload.GetProperty("status").GetProperty("type").GetString().ShouldBe("idle");
        status.Payload.GetProperty("capabilities").GetProperty("canPrompt").GetBoolean().ShouldBeTrue();
        var activity = _builder.EventBroadcaster.Broadcasts.Where(b => b.Topic == "sessions" && b.Type == "activity_status").ShouldHaveSingleItem();
        activity.Payload.GetProperty("activityStatus").GetString().ShouldBe("idle");
    }

    [Fact]
    public async Task a_running_session_is_not_woken_again()
    {
        SeedSession(resumeToken: "token-saved");
        var running = new FakeHarnessSession("inst-asleep");
        _builder.InstanceTracker.Register("inst-asleep", running);
        var sut = _builder.Build();

        var result = await sut.ActivateSessionAsync(SessionId);

        result.Value.ShouldBeSameAs(running);
        _runtime.SpawnCalls.ShouldBeEmpty();
        _runtime.ResumeCalls.ShouldBeEmpty();
        _builder.EventBroadcaster.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_session_that_is_gone_is_not_found()
    {
        var sut = _builder.Build();

        var result = await sut.ActivateSessionAsync("sess-gone");

        result.Error.Code.ShouldEndWith(".NotFound");
    }

    [Fact]
    public async Task a_session_whose_workspace_is_gone_fails_to_wake_and_is_marked_as_errored()
    {
        SeedSession(resumeToken: "token-saved", workspaceId: "ws-gone");
        var sut = _builder.Build();

        var result = await sut.ActivateSessionAsync(SessionId);

        result.IsFailure.ShouldBeTrue();
        _runtime.ResumeCalls.ShouldBeEmpty();
        var stored = await _builder.SessionRepository.GetByIdAsync(SessionId);
        stored!.Status.ShouldBe("error");
        _builder.EventBroadcaster.Broadcasts.ShouldContain(b =>
            b.Topic == $"session:{SessionId}" && b.Type == "session.status" && b.Payload.GetProperty("lifecycleStatus").GetString() == "error");
    }

    [Fact]
    public async Task a_woken_session_is_told_its_permission_level()
    {
        SeedSession(resumeToken: "token-saved");
        _builder.UserPreferenceRepository.Seed(SessionPermissions.LevelKey, PermissionLevels.Ask);
        var sut = _builder.Build();

        await sut.ActivateSessionAsync(SessionId);

        _woken.AppliedPermissions.ShouldHaveSingleItem().ShouldBe(new PermissionPolicy(PermissionLevels.Ask));
    }

    [Fact]
    public async Task every_prompt_tells_the_harness_the_permission_level_as_it_is_now()
    {
        SeedSession(resumeToken: "token-saved");
        var running = new FakeHarnessSession("inst-asleep");
        _builder.InstanceTracker.Register("inst-asleep", running);
        var sut = _builder.Build();

        await sut.PromptSessionAsync(SessionId, "first");
        _builder.UserPreferenceRepository.Seed(SessionPermissions.HarnessLevelKey("opencode"), PermissionLevels.Edits);
        await sut.PromptSessionAsync(SessionId, "second");

        running.AppliedPermissions.ShouldBe([PermissionPolicy.AllowAll, new PermissionPolicy(PermissionLevels.Edits)]);
        running.SendPromptCalls.Count.ShouldBe(2);
    }
}
