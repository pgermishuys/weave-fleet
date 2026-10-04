using Shouldly;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// Fork copies a session's conversation in its harness up to the last finished turn, and the copy becomes a session of
/// its own in the same folder. A harness that can't copy a conversation has no Fork, rather than an empty session.
/// </summary>
public sealed class SessionOrchestratorForkTests : IAsyncDisposable
{
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly FakeHarnessSession _parent = new("inst-parent");
    private readonly FakeHarnessSession _fork = new("inst-fork");
    private FakeHarnessRuntime _runtime = null!;

    public async ValueTask DisposeAsync()
    {
        await _parent.DisposeAsync();
        await _fork.DisposeAsync();
    }

    private void Seed(bool supportsForking = true, string harnessType = "opencode", string displayName = "OpenCode")
    {
        _runtime = _builder.RegisterHarness(harnessType, displayName, new HarnessCapabilities { SupportsForking = supportsForking, SupportsResume = true });
        _runtime.ResumeBehavior = (_, _) => Task.FromResult<IHarnessSession>(_fork);
        _builder.WorkspaceRepository.Seed(new Workspace { Id = "ws-1", Directory = "/tmp/repo", IsolationStrategy = "existing" });
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1",
            WorkspaceId = "ws-1",
            InstanceId = "inst-parent",
            ProjectId = "proj-1",
            Title = "Refactor the parser",
            Status = "active",
            Directory = "/tmp/repo",
            CreatedAt = "2026-01-01",
            HarnessType = harnessType,
            HarnessProfileId = "work",
            HarnessResumeToken = "ses_parent",
            UserId = "user-1",
            SelectedAgent = "build",
            SelectedProviderId = "anthropic",
            SelectedModelId = "claude-sonnet",
        });
        _builder.HarnessProfileRepository.Seed(new HarnessProfile
        {
            Id = "work", HarnessType = harnessType, Name = "Work", Content = "{}", UserId = "user-1",
        });
        _builder.InstanceTracker.Register("inst-parent", _parent);
        _parent.ConversationFork = new ConversationFork("ses_fork", "msg_last");
    }

    [Fact]
    public async Task forks_the_harness_session_then_attaches_a_new_session_to_the_copy()
    {
        Seed();
        var orchestrator = _builder.Build();

        var result = await orchestrator.ForkSessionAsync("s1");

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        _parent.ConversationForks.ShouldBe(1);

        var resume = _runtime.ResumeCalls.ShouldHaveSingleItem();
        resume.ResumeToken.ShouldBe("ses_fork");
        resume.SessionId.ShouldBe(result.Value.Session.Id);
        // On the process the parent runs on, prepared with its profile: that's where the copy lives.
        resume.ParentSessionId.ShouldBe("s1");
        resume.WorkingDirectory.ShouldBe("/tmp/repo");
        _runtime.PrepareCalls.ShouldHaveSingleItem().Profile.ShouldNotBeNull().Id.ShouldBe("work");

        var fork = result.Value.Session;
        fork.HarnessResumeToken.ShouldBe("ses_fork");
        fork.InstanceId.ShouldBe("inst-fork");
        result.Value.InstanceId.ShouldBe("inst-fork");
        _builder.InstanceTracker.Get("inst-fork").ShouldBeSameAs(_fork);
    }

    [Fact]
    public async Task the_fork_keeps_the_parents_harness_profile_agent_model_and_folder()
    {
        Seed();
        var orchestrator = _builder.Build();

        var fork = (await orchestrator.ForkSessionAsync("s1")).Value.Session;

        fork.Title.ShouldBe("Fork of Refactor the parser");
        fork.HarnessType.ShouldBe("opencode");
        fork.HarnessProfileId.ShouldBe("work");
        fork.SelectedAgent.ShouldBe("build");
        fork.SelectedProviderId.ShouldBe("anthropic");
        fork.SelectedModelId.ShouldBe("claude-sonnet");
        fork.ProjectId.ShouldBe("proj-1");
        fork.Directory.ShouldBe("/tmp/repo");
        fork.UserId.ShouldBe("user-1");
        // A session of its own in the list, not a hidden child or side conversation.
        fork.IsHidden.ShouldBeFalse();
        fork.ParentSessionId.ShouldBeNull();
        fork.SideOfSessionId.ShouldBeNull();
        // Its own workspace over the same folder, so deleting either session leaves the other's folder alone.
        fork.WorkspaceId.ShouldNotBe("ws-1");
        _builder.SessionRepository.InsertedSessions.ShouldContain(s => s.Id == fork.Id);
        _builder.EventBroadcaster.Broadcasts.ShouldContain(b => b.Type == "session_created" && b.Topic == "sessions");
    }

    [Fact]
    public async Task the_fork_remembers_the_session_it_was_forked_from()
    {
        Seed();
        var orchestrator = _builder.Build();

        var fork = (await orchestrator.ForkSessionAsync("s1")).Value.Session;

        fork.ForkedFromSessionId.ShouldBe("s1");
        fork.SpawnKind.ShouldBe(SpawnKinds.Fork);
        // The user forked it; no session's agent started it.
        fork.SpawnedBySessionId.ShouldBeNull();
        _builder.SessionRepository.InsertedSessions.Single(s => s.Id == fork.Id).ForkedFromSessionId.ShouldBe("s1");
        var created = _builder.EventBroadcaster.Broadcasts.Single(b => b.Type == "session_created");
        created.Payload.GetProperty("forkedFromSessionId").GetString().ShouldBe("s1");
        created.Payload.GetProperty("spawnKind").GetString().ShouldBe("fork");
    }

    [Fact]
    public async Task a_given_title_names_the_fork()
    {
        Seed();
        var orchestrator = _builder.Build();

        var fork = (await orchestrator.ForkSessionAsync("s1", "  Try the other parser  ")).Value.Session;

        fork.Title.ShouldBe("Try the other parser");
    }

    [Fact]
    public async Task the_parent_is_left_alone()
    {
        Seed();
        var orchestrator = _builder.Build();

        await orchestrator.ForkSessionAsync("s1");

        _parent.SendPromptCalls.ShouldBeEmpty();
        var parent = await _builder.SessionRepository.GetByIdAsync("s1");
        parent.ShouldNotBeNull().HarnessResumeToken.ShouldBe("ses_parent");
        _builder.InstanceTracker.Get("inst-parent").ShouldBeSameAs(_parent);
    }

    [Fact]
    public async Task a_harness_that_cant_fork_is_refused_with_why_and_makes_nothing()
    {
        Seed(supportsForking: false, harnessType: "claude-code", displayName: "Claude Code");
        var orchestrator = _builder.Build();

        var result = await orchestrator.ForkSessionAsync("s1");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldStartWith("Validation.");
        result.Error.Description.ShouldBe("Claude Code can't copy a conversation, so its sessions can't be forked.");
        _parent.ConversationForks.ShouldBe(0);
        _runtime.ResumeCalls.ShouldBeEmpty();
        _runtime.SpawnCalls.ShouldBeEmpty();
        _builder.SessionRepository.InsertedSessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_fork_the_harness_couldnt_make_is_an_error_not_an_empty_session()
    {
        Seed();
        _parent.ConversationFork = null;
        var orchestrator = _builder.Build();

        var result = await orchestrator.ForkSessionAsync("s1");

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe("OpenCode couldn't fork this session.");
        _runtime.ResumeCalls.ShouldBeEmpty();
        _runtime.SpawnCalls.ShouldBeEmpty();
        _builder.SessionRepository.InsertedSessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task an_unknown_session_is_not_found()
    {
        Seed();
        var orchestrator = _builder.Build();

        var result = await orchestrator.ForkSessionAsync("nope");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldEndWith(".NotFound");
    }
}
