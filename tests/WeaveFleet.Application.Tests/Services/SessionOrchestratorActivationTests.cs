using Shouldly;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fixtures;

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

    [Fact]
    public async Task a_session_whose_checkout_was_removed_gets_it_back_at_the_same_path_before_its_harness_starts()
    {
        using var repository = new RealGitRepository();
        var directory = repository.CreateWorktree("fleet/export-csv");
        repository.Git("worktree", "remove", directory);
        SeedRemovedCheckout(repository, directory, "fleet/export-csv");
        SeedSession(resumeToken: "token-saved", workspaceId: "ws-removed");
        var sut = _builder.Build();

        var result = await sut.ActivateSessionAsync(SessionId);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        repository.Git("-C", directory, "branch", "--show-current").Trim().ShouldBe("fleet/export-csv");
        _runtime.ResumeCalls.ShouldHaveSingleItem().WorkingDirectory.ShouldBe(directory);
        (await _builder.WorkspaceRepository.GetByIdAsync("ws-removed"))!.CleanedUpAt.ShouldBeNull();
        _builder.EventBroadcaster.Broadcasts.Where(b => b.Type == "session_checkout")
            .Select(b => b.Payload.GetProperty("state").GetString()).ShouldBe(["restoring", "restored"]);
    }

    [Fact]
    public async Task a_removed_checkout_whose_branch_is_gone_fails_the_wake_with_a_plain_message()
    {
        using var repository = new RealGitRepository();
        var directory = repository.CreateWorktree("fleet/deleted-since");
        repository.Git("worktree", "remove", directory);
        repository.Git("branch", "-D", "fleet/deleted-since");
        SeedRemovedCheckout(repository, directory, "fleet/deleted-since");
        SeedSession(resumeToken: "token-saved", workspaceId: "ws-removed");
        var sut = _builder.Build();

        var result = await sut.ActivateSessionAsync(SessionId);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe("The branch fleet/deleted-since isn't there any more. The conversation is kept.");
        _runtime.ResumeCalls.ShouldBeEmpty();
        (await _builder.WorkspaceRepository.GetByIdAsync("ws-removed"))!.CleanedUpAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task reading_a_session_whose_checkout_was_removed_leaves_it_asleep_and_removed()
    {
        using var repository = new RealGitRepository();
        var directory = repository.CreateWorktree("fleet/read-only");
        repository.Git("worktree", "remove", directory);
        SeedRemovedCheckout(repository, directory, "fleet/read-only");
        SeedSession(resumeToken: "token-saved", workspaceId: "ws-removed");
        _builder.Build();

        var result = await _builder.Activation.ActivateToReadAsync(SessionId);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Session.CheckoutRemoved");
        Directory.Exists(directory).ShouldBeFalse();
        _runtime.ResumeCalls.ShouldBeEmpty();
        (await _builder.SessionRepository.GetByIdAsync(SessionId))!.Status.ShouldNotBe("error");
    }

    [Fact]
    public async Task reading_a_session_whose_harness_keeps_the_conversation_puts_its_checkout_back_to_read_it()
    {
        using var repository = new RealGitRepository();
        var directory = repository.CreateWorktree("fleet/kept-in-harness");
        repository.Git("worktree", "remove", directory);
        SeedRemovedCheckout(repository, directory, "fleet/kept-in-harness");
        var keepsHistory = _builder.RegisterHarness("opencode2", "OpenCode 2", new HarnessCapabilities { SupportsResume = true, HistoryLivesInHarness = true });
        keepsHistory.DefaultSession = _woken;
        SeedSession(resumeToken: "token-saved", workspaceId: "ws-removed");
        (await _builder.SessionRepository.GetByIdAsync(SessionId))!.HarnessType = "opencode2";
        _builder.Build();

        var result = await _builder.Activation.ActivateToReadAsync(SessionId);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        repository.Git("-C", directory, "branch", "--show-current").Trim().ShouldBe("fleet/kept-in-harness");
        keepsHistory.ResumeCalls.ShouldHaveSingleItem().WorkingDirectory.ShouldBe(directory);
    }

    private void SeedRemovedCheckout(RealGitRepository repository, string directory, string branch) => _builder.WorkspaceRepository.Seed(new Workspace
    {
        Id = "ws-removed", Directory = directory, SourceDirectory = repository.Path, IsolationStrategy = "worktree", Branch = branch,
        CreatedAt = "2026-10-01", CleanedUpAt = "2026-10-08 09:00:00", UserId = "user-1",
    });
}
