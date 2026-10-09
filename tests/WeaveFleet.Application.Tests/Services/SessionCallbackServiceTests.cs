using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// A session started with <c>onComplete</c> prompts the session that asked once it's done. The callback is kept until
/// that prompt was delivered: a Fleet restart (the instance id it was registered with is gone), a target that isn't
/// running, or a prompt that fails leaves it for the next try.
/// </summary>
public sealed class SessionCallbackServiceTests : IAsyncDisposable
{
    private const string UserId = "user-1";
    private const string Source = "s-worker";
    private const string Target = "s-coordinator";

    private readonly TestUserContext _user = new(UserId);
    private readonly SessionOrchestratorBuilder _builder;
    private readonly FakeHarnessRuntime _runtime;
    private readonly FakeHarnessSession _live = new("inst-live");

    public SessionCallbackServiceTests()
    {
        _builder = new SessionOrchestratorBuilder().WithUserContext(_user);
        _runtime = _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true });
        _builder.WorkspaceRepository.Seed(new Workspace
        {
            Id = "ws-1",
            Directory = "/tmp/coordinator",
            IsolationStrategy = "existing",
            CreatedAt = "2026-01-01",
            UserId = UserId,
        });
        SeedSession(Source, "Write the tests", instanceId: "inst-worker");
    }

    public ValueTask DisposeAsync() => _live.DisposeAsync();

    private void SeedSession(string id, string title, string instanceId, string userId = UserId, string retentionStatus = "active")
        => _builder.SessionRepository.Seed(new Session
        {
            Id = id,
            WorkspaceId = "ws-1",
            InstanceId = instanceId,
            Title = title,
            Status = "active",
            Directory = "/tmp/coordinator",
            CreatedAt = "2026-01-01",
            RetentionStatus = retentionStatus,
            HarnessType = "opencode",
            RuntimeMode = "automatic",
            HarnessResumeToken = $"resume-{id}",
            UserId = userId,
        });

    /// <summary>The coordinator, running on <c>inst-live</c> now.</summary>
    private void SeedLiveTarget()
    {
        SeedSession(Target, "Coordinator", instanceId: "inst-live");
        _builder.InstanceTracker.Register("inst-live", _live);
    }

    /// <summary>A callback registered before a restart: its instance id is one Fleet no longer runs.</summary>
    private SessionCallback Register(string status = SessionCallbackStatuses.Started, string target = Target)
    {
        var callback = new SessionCallback
        {
            Id = $"cb-{Guid.NewGuid():N}",
            SourceSessionId = Source,
            TargetSessionId = target,
            TargetInstanceId = "inst-before-restart",
            Status = status,
            CreatedAt = "2026-01-01T00:00:00Z",
        };
        _builder.SessionCallbackRepository.Seed(callback);
        return callback;
    }

    private SessionCallbackService Build() => new(
        _builder.SessionCallbackRepository,
        _builder.SessionRepository,
        _builder.Build(),
        _builder.ActivityTracker,
        NullLogger<SessionCallbackService>.Instance);

    private const string Completion = "Session 'Write the tests' (s-worker) completed.";

    [Fact]
    public async Task A_callback_registered_before_a_restart_goes_to_the_target_sessions_current_instance()
    {
        SeedLiveTarget();
        var callback = Register();

        var fired = await Build().ProcessPendingCallbacksAsync();

        fired.ShouldBe(1);
        _live.SendPromptCalls.ShouldHaveSingleItem().Text.ShouldBe(Completion);
        callback.Status.ShouldBe(SessionCallbackStatuses.Fired);
        callback.FiredAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_target_with_no_live_instance_is_resumed_and_prompted_as_any_prompt_would()
    {
        SeedSession(Target, "Coordinator", instanceId: "inst-before-restart");
        await using var resumed = new FakeHarnessSession("inst-resumed");
        _runtime.DefaultSession = resumed;
        var callback = Register();

        var fired = await Build().ProcessPendingCallbacksAsync();

        fired.ShouldBe(1);
        _runtime.ResumeCalls.ShouldHaveSingleItem().SessionId.ShouldBe(Target);
        resumed.SendPromptCalls.ShouldHaveSingleItem().Text.ShouldBe(Completion);
        callback.Status.ShouldBe(SessionCallbackStatuses.Fired);
    }

    [Fact]
    public async Task A_target_that_cant_be_resumed_keeps_the_callback_until_a_later_poll_delivers_it()
    {
        SeedSession(Target, "Coordinator", instanceId: "inst-before-restart");
        await using var resumed = new FakeHarnessSession("inst-resumed");
        var harnessUp = false;
        _runtime.ResumeBehavior = (_, _) => harnessUp
            ? Task.FromResult<IHarnessSession>(resumed)
            : throw new InvalidOperationException("OpenCode isn't running.");
        var callback = Register();

        (await Build().ProcessPendingCallbacksAsync()).ShouldBe(0);

        callback.Status.ShouldBe(SessionCallbackStatuses.Started);
        callback.FiredAt.ShouldBeNull();

        harnessUp = true;
        (await Build().ProcessPendingCallbacksAsync()).ShouldBe(1);

        resumed.SendPromptCalls.ShouldHaveSingleItem().Text.ShouldBe(Completion);
        callback.Status.ShouldBe(SessionCallbackStatuses.Fired);
    }

    [Fact]
    public async Task A_prompt_that_fails_leaves_the_callback_for_the_next_try()
    {
        SeedLiveTarget();
        var fail = true;
        _live.SendPromptBehavior = (_, _, _) => fail ? throw new HttpRequestException("connection refused") : Task.CompletedTask;
        var callback = Register();

        (await Build().ProcessPendingCallbacksAsync()).ShouldBe(0);
        callback.Status.ShouldBe(SessionCallbackStatuses.Started);
        callback.FiredAt.ShouldBeNull();

        fail = false;
        (await Build().ProcessPendingCallbacksAsync()).ShouldBe(1);
        callback.Status.ShouldBe(SessionCallbackStatuses.Fired);
    }

    [Fact]
    public async Task A_target_that_isnt_the_source_owners_session_is_never_prompted()
    {
        SeedSession("s-someone-else", "Theirs", instanceId: "inst-theirs", userId: "user-2");
        await using var theirs = new FakeHarnessSession("inst-theirs");
        _builder.InstanceTracker.Register("inst-theirs", theirs);
        var callback = Register(target: "s-someone-else");

        (await Build().ProcessPendingCallbacksAsync()).ShouldBe(0);

        theirs.SendPromptCalls.ShouldBeEmpty();
        _runtime.ResumeCalls.ShouldBeEmpty();
        callback.Status.ShouldBe(SessionCallbackStatuses.Started);
    }

    [Fact]
    public async Task A_source_that_hasnt_started_working_doesnt_fire()
    {
        SeedLiveTarget();
        var callback = Register(status: SessionCallbackStatuses.Pending);

        (await Build().ProcessPendingCallbacksAsync()).ShouldBe(0);

        _live.SendPromptCalls.ShouldBeEmpty();
        callback.Status.ShouldBe(SessionCallbackStatuses.Pending);
    }

    [Fact]
    public async Task A_source_still_in_its_turn_doesnt_fire_until_its_turn_ends()
    {
        SeedLiveTarget();
        var callback = Register();
        _builder.ActivityTracker.Update(Source, ActivityStatuses.Busy, UserId);
        var service = Build();

        (await service.ProcessPendingCallbacksAsync()).ShouldBe(0);
        _live.SendPromptCalls.ShouldBeEmpty();

        // The idle event is the signal; the tracker may not have caught up yet.
        (await service.OnSessionIdledAsync(Source)).ShouldBe(1);
        callback.Status.ShouldBe(SessionCallbackStatuses.Fired);
    }

    [Fact]
    public async Task A_busy_target_hears_when_its_own_turn_ends()
    {
        SeedLiveTarget();
        var callback = Register();
        _builder.ActivityTracker.Update(Target, ActivityStatuses.Busy, UserId);
        var service = Build();

        (await service.OnSessionIdledAsync(Source)).ShouldBe(0);
        _live.SendPromptCalls.ShouldBeEmpty();
        callback.Status.ShouldBe(SessionCallbackStatuses.Started);

        (await service.OnSessionIdledAsync(Target)).ShouldBe(1);
        _live.SendPromptCalls.ShouldHaveSingleItem().Text.ShouldBe(Completion);
    }

    [Fact]
    public async Task An_archived_target_waits_until_it_is_restored()
    {
        SeedSession(Target, "Coordinator", instanceId: "inst-live", retentionStatus: "archived");
        _builder.InstanceTracker.Register("inst-live", _live);
        var callback = Register();

        (await Build().ProcessPendingCallbacksAsync()).ShouldBe(0);

        _live.SendPromptCalls.ShouldBeEmpty();
        callback.Status.ShouldBe(SessionCallbackStatuses.Started);
    }

    [Fact]
    public async Task It_fires_once()
    {
        SeedLiveTarget();
        Register();
        var service = Build();

        await service.OnSessionIdledAsync(Source);
        await service.OnSessionIdledAsync(Source);
        await service.ProcessPendingCallbacksAsync();

        _live.SendPromptCalls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_relay_marks_the_source_started_on_its_reply_and_fires_on_its_idle()
    {
        SeedLiveTarget();
        var callback = Register(status: SessionCallbackStatuses.Pending);
        var dispatcher = BuildDispatcher();

        // Idle before any reply: the session hasn't worked, so nothing is done yet.
        dispatcher.Observe(Source, UserId, Idled(Source));
        await dispatcher.Pending;
        callback.Status.ShouldBe(SessionCallbackStatuses.Pending);

        dispatcher.Observe(Source, UserId, Reply(Source));
        dispatcher.Observe(Source, UserId, Reply(Source));
        await dispatcher.Pending;
        callback.Status.ShouldBe(SessionCallbackStatuses.Started);

        dispatcher.Observe(Source, UserId, Idled(Source));
        await dispatcher.Pending;

        _live.SendPromptCalls.ShouldHaveSingleItem().Text.ShouldBe(Completion);
        callback.Status.ShouldBe(SessionCallbackStatuses.Fired);
    }

    [Fact]
    public async Task The_poll_fires_a_callback_whose_source_finished_while_Fleet_was_down()
    {
        // Saved as started before the restart; no idle event will come for that turn.
        SeedLiveTarget();
        var callback = Register();
        _builder.SessionCallbackRepository.Owners.Add(UserId);

        await BuildDispatcher().ProcessPendingAsync(CancellationToken.None);

        _live.SendPromptCalls.ShouldHaveSingleItem().Text.ShouldBe(Completion);
        callback.Status.ShouldBe(SessionCallbackStatuses.Fired);
    }

    private SessionCallbackDispatcher BuildDispatcher()
    {
        var orchestrator = _builder.Build();
        return new SessionCallbackDispatcher(
            TestServiceScopeFactory.Create(services =>
            {
                services.AddSingleton<IBackgroundUserScope>(new NoUserScope());
                services.AddSingleton<ISessionCallbackRepository>(_builder.SessionCallbackRepository);
                services.AddSingleton(_ => new SessionCallbackService(
                    _builder.SessionCallbackRepository,
                    _builder.SessionRepository,
                    orchestrator,
                    _builder.ActivityTracker,
                    NullLogger<SessionCallbackService>.Instance));
            }),
            NullLogger<SessionCallbackDispatcher>.Instance);
    }

    private static MessageUpdated Reply(string sessionId) => new()
    {
        Payload = new MessageLifecyclePayload
        {
            Info = new MessageEventInfo
            {
                Id = $"reply_{Guid.NewGuid():N}",
                Role = "assistant",
                SessionId = sessionId,
                Time = new MessageEventTime { Created = 0 },
            },
        },
    };

    private static SessionIdled Idled(string sessionId) => new() { Payload = new SessionIdledPayload { SessionId = sessionId } };

    private sealed class NoUserScope : IBackgroundUserScope
    {
        public IDisposable Begin(string userId) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
