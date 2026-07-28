using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Projections;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.IntegrationTests.Sessions;

public sealed class AutoActivationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task automatic_stopped_session_auto_activates_on_prompt_and_persists_response()
    {
        using var directory = new TempDirectory();
        var builder = CreateBuilder();
        var runtime = builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true });
        var tracker = new InstanceTracker();
        var publisher = new ProjectingEventPublisher(builder);
        var sut = CreateOrchestrator(builder, tracker, publisher, pooledOpenCodeHarness: true);
        var initialHarness = new FakeHarnessSession("inst-initial") { ResumeToken = "oc-auto-resume" };
        runtime.DefaultSession = initialHarness;

        var createResult = await sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = directory.Path,
            Title = "Automatic session",
            HarnessType = "opencode",
        }, CancellationToken.None);
        createResult.IsSuccess.ShouldBeTrue();

        var sessionId = createResult.Value.Session.Id;
        await builder.SessionRepository.UpdateResumeTokenAsync(sessionId, "oc-auto-resume");
        var stopResult = await sut.StopSessionAsync(sessionId, CancellationToken.None);
        stopResult.IsSuccess.ShouldBeTrue();

        var resumedHarness = new FakeHarnessSession("inst-resumed") { ResumeToken = "oc-auto-resume" };
        runtime.DefaultSession = resumedHarness;

        var promptResult = await sut.PromptSessionAsync(sessionId, "hello after restart", options: null, CancellationToken.None);
        promptResult.IsSuccess.ShouldBeTrue();

        runtime.ResumeCalls.Count.ShouldBe(1);
        runtime.ResumeCalls[0].SessionId.ShouldBe(sessionId);
        runtime.ResumeCalls[0].ResumeToken.ShouldBe("oc-auto-resume");
        resumedHarness.SendPromptCalls.Count.ShouldBe(1);
        resumedHarness.SendPromptCalls[0].Text.ShouldBe("hello after restart");
        tracker.Get("inst-resumed").ShouldBeSameAs(resumedHarness);

        await publisher.ProjectAsync(
            sessionId,
            createResult.Value.Session.UserId,
            CreateAssistantResponseEvent("oc-auto-resume", "auto-response", "response arrived"),
            CancellationToken.None);

        var messages = await builder.MessageRepository.GetBySessionAsync(sessionId, 10, null);
        messages.ShouldContain(message => message.Role == "user" && message.PartsJson.Contains("hello after restart", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Role == "assistant" && message.PartsJson.Contains("response arrived", StringComparison.Ordinal));
        var stored = await builder.SessionRepository.GetByIdAsync(sessionId);
        stored.ShouldNotBeNull();
        stored.InstanceId.ShouldBe("inst-resumed");
        stored.LifecycleStatus.ShouldBe("running");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task automatic_running_pooled_session_auto_activates_on_prompt_after_fleet_restart_without_resume()
    {
        using var directory = new TempDirectory();
        var builder = CreateBuilder();
        var runtime = builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true });
        var initialTracker = new InstanceTracker();
        var publisher = new ProjectingEventPublisher(builder);
        var initialOrchestrator = CreateOrchestrator(builder, initialTracker, publisher, pooledOpenCodeHarness: true);
        var initialHarness = new FakeHarnessSession("inst-restart-initial") { ResumeToken = "oc-restart-resume" };
        runtime.DefaultSession = initialHarness;

        var createResult = await initialOrchestrator.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = directory.Path,
            Title = "Automatic restart session",
            HarnessType = "opencode",
        }, CancellationToken.None);
        createResult.IsSuccess.ShouldBeTrue();

        var sessionId = createResult.Value.Session.Id;
        await builder.SessionRepository.UpdateResumeTokenAsync(sessionId, "oc-restart-resume");
        await builder.SessionRepository.UpdateForResumeAsync(sessionId, "inst-restart-initial");
        var instanceService = new InstanceService(
            builder.InstanceRepository,
            builder.SessionRepository,
            new TestUserContext("user-1"));
        var stoppedInstanceCount = await instanceService.MarkAllStoppedAsync();
        var stoppedSessionCount = await instanceService.MarkAllNonTerminalSessionsStoppedAsync();

        stoppedInstanceCount.ShouldBe(1);
        stoppedSessionCount.ShouldBe(0);
        var afterRecovery = await builder.SessionRepository.GetByIdAsync(sessionId);
        afterRecovery.ShouldNotBeNull();
        afterRecovery.RuntimeMode.ShouldBe("automatic");
        afterRecovery.LifecycleStatus.ShouldBe("running");
        afterRecovery.StoppedAt.ShouldBeNull();

        var restartedTracker = new InstanceTracker();
        var restartedOrchestrator = CreateOrchestrator(builder, restartedTracker, publisher, pooledOpenCodeHarness: true);
        var resumedHarness = new FakeHarnessSession("inst-restart-resumed") { ResumeToken = "oc-restart-resume" };
        runtime.DefaultSession = resumedHarness;

        var promptResult = await restartedOrchestrator.PromptSessionAsync(
            sessionId,
            "hello after fleet restart",
            options: null,
            CancellationToken.None);

        promptResult.IsSuccess.ShouldBeTrue();
        runtime.ResumeCalls.Count.ShouldBe(1);
        runtime.ResumeCalls[0].SessionId.ShouldBe(sessionId);
        runtime.ResumeCalls[0].ResumeToken.ShouldBe("oc-restart-resume");
        resumedHarness.SendPromptCalls.Count.ShouldBe(1);
        resumedHarness.SendPromptCalls[0].Text.ShouldBe("hello after fleet restart");
        restartedTracker.Get("inst-restart-resumed").ShouldBeSameAs(resumedHarness);
        initialTracker.Get("inst-restart-initial").ShouldBeSameAs(initialHarness);

        var messages = await builder.MessageRepository.GetBySessionAsync(sessionId, 10, null);
        messages.ShouldContain(message => message.Role == "user" && message.PartsJson.Contains("hello after fleet restart", StringComparison.Ordinal));
        var stored = await builder.SessionRepository.GetByIdAsync(sessionId);
        stored.ShouldNotBeNull();
        stored.InstanceId.ShouldBe("inst-restart-resumed");
        stored.LifecycleStatus.ShouldBe("running");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task manual_stopped_session_prompt_returns_instance_not_found_error()
    {
        using var directory = new TempDirectory();
        var builder = CreateManualBuilder();
        var runtime = builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true });
        var tracker = new InstanceTracker();
        var sut = CreateOrchestrator(builder, tracker, new FakeEventPublisher(), pooledOpenCodeHarness: false);
        runtime.DefaultSession = new FakeHarnessSession("inst-manual-initial") { ResumeToken = "manual-resume" };

        var createResult = await sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = directory.Path,
            Title = "Manual session",
            HarnessType = "opencode",
        }, CancellationToken.None);
        createResult.IsSuccess.ShouldBeTrue();

        var sessionId = createResult.Value.Session.Id;
        await builder.SessionRepository.UpdateResumeTokenAsync(sessionId, "manual-resume");
        var stopResult = await sut.StopSessionAsync(sessionId, CancellationToken.None);
        stopResult.IsSuccess.ShouldBeTrue();

        runtime.DefaultSession = new FakeHarnessSession("inst-manual-resumed") { ResumeToken = "manual-resume" };
        var promptResult = await sut.PromptSessionAsync(sessionId, "hello manual", options: null, CancellationToken.None);

        promptResult.IsFailure.ShouldBeTrue();
        promptResult.Error.Code.ShouldBe("Instance.NotFound");
        runtime.ResumeCalls.ShouldBeEmpty();
        runtime.DefaultSession.SendPromptCalls.ShouldBeEmpty();
        tracker.Get("inst-manual-resumed").ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task create_session_eager_pooled_path_persists_harness_resume_token_on_initial_insert()
    {
        // Verifies the eager create-session ordering for pooled sessions:
        //   (b) workspace directory is authorized (already enforced by session source resolution)
        //   (c) the harness session is created before the Fleet session row is inserted
        //   (d) HarnessResumeToken is populated on the INITIAL Fleet session INSERT, not lazily
        //
        // Warm scratch directories (used by WarmupPooledInstanceAsync) are never the same as the
        // real canonicalized workspace directory supplied to CreateSessionAsync.

        using var directory = new TempDirectory();
        var builder = CreateBuilder();
        var runtime = builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true });
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var sut = CreateOrchestrator(builder, tracker, publisher, pooledOpenCodeHarness: true);

        // Simulate a harness session that carries an eagerly-created OC session id.
        var initialHarness = new FakeHarnessSession("inst-eager-initial") { ResumeToken = "oc-eager-token" };
        runtime.DefaultSession = initialHarness;

        var createResult = await sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = directory.Path,
            Title = "Eager pooled session",
            HarnessType = "opencode",
        }, CancellationToken.None);

        createResult.IsSuccess.ShouldBeTrue();
        var sessionId = createResult.Value.Session.Id;

        // (d) HarnessResumeToken must be on the inserted row immediately after create-session.
        var stored = await builder.SessionRepository.GetByIdAsync(sessionId);
        stored.ShouldNotBeNull();
        stored.HarnessResumeToken.ShouldBe("oc-eager-token");

        // The runtime mode should be "automatic" because pooledOpenCodeHarness is enabled.
        stored.RuntimeMode.ShouldBe("automatic");

        // Spawn was called exactly once (no lazy defer to first prompt).
        runtime.SpawnCalls.Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task create_session_directory_authorization_rejects_before_spawn_in_pooled_mode()
    {
        // Validates that ownership/directory validation failures happen before any
        // lease or OpenCode session creation (acceptance criterion).
        var builder = CreateBuilder();
        var runtime = builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true });
        var tracker = new InstanceTracker();
        var sut = CreateOrchestrator(builder, tracker, new FakeEventPublisher(), pooledOpenCodeHarness: true);
        runtime.DefaultSession = new FakeHarnessSession("inst-auth-initial");

        // Directory outside all allowed workspace roots: should fail before spawn.
        var createResult = await sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = "/nonexistent-unauthorized-path/xyz",
            Title = "Unauthorized session",
            HarnessType = "opencode",
        }, CancellationToken.None);

        createResult.IsFailure.ShouldBeTrue();
        // No spawn calls — rejection happened before pool acquisition or OC session creation.
        runtime.SpawnCalls.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task create_session_db_insert_failure_rolls_back_harness_session_and_does_not_persist_token()
    {
        // Full integration test for the DB insert rollback path:
        //   1. SpawnAsync creates the harness session (and in pooled mode, the OC session eagerly).
        //   2. DB insert fails (simulated via InsertBehavior).
        //   3. The orchestrator must best-effort DeleteAsync the harness session (to clean up the
        //      OC session and release the pooled lease) and must NOT persist the resume token.
        using var directory = new TempDirectory();
        var builder = CreateBuilder();
        var runtime = builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true });
        var tracker = new InstanceTracker();
        var sut = CreateOrchestrator(builder, tracker, new FakeEventPublisher(), pooledOpenCodeHarness: true);

        var deleteCalled = false;
        await using var harness = new FakeHarnessSession("inst-rollback") { ResumeToken = "oc-rollback-token" };
        harness.DeleteBehavior = _ =>
        {
            deleteCalled = true;
            return Task.CompletedTask;
        };
        runtime.DefaultSession = harness;

        // Inject a DB insert failure.
        builder.SessionRepository.InsertBehavior = _ => throw new InvalidOperationException("DB unavailable");

        // The create call must propagate the DB exception.
        await Should.ThrowAsync<InvalidOperationException>(() => sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = directory.Path,
            Title = "Rollback session",
            HarnessType = "opencode",
        }, CancellationToken.None));

        // DeleteAsync must have been called to clean up the OC session.
        deleteCalled.ShouldBeTrue();
        // The in-memory tracker must NOT hold the instance after rollback.
        tracker.Get("inst-rollback").ShouldBeNull();
        // No session row must have been persisted — the resume token was not committed.
        builder.SessionRepository.InsertedSessions.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task stale_resume_token_after_crash_restart_rehydrates_to_fresh_live_oc_session()
    {
        // After a Fleet restart, the in-memory state is gone. On the next prompt, Fleet must:
        //   (a) Resume the session using the persisted HarnessResumeToken.
        //   (b) The runtime detects the token is stale (OC session not found) and creates a new one.
        //   (c) The fresh OC session ID is persisted, so subsequent resumes use the live session.
        using var directory = new TempDirectory();
        var builder = CreateBuilder();
        var runtime = builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true });
        var initialTracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var initialOrchestrator = CreateOrchestrator(builder, initialTracker, publisher, pooledOpenCodeHarness: true);

        // Create a session and simulate "crash" — the runtime has a stale in-memory token.
        var staleToken = "stale-oc-session-pre-crash";
        var initialHarness = new FakeHarnessSession("inst-crash-initial") { ResumeToken = staleToken };
        runtime.DefaultSession = initialHarness;

        var createResult = await initialOrchestrator.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = directory.Path,
            Title = "Crash recovery session",
            HarnessType = "opencode",
        }, CancellationToken.None);
        createResult.IsSuccess.ShouldBeTrue();
        var sessionId = createResult.Value.Session.Id;

        // Write the stale token into the DB as if it was eagerly persisted before crash.
        await builder.SessionRepository.UpdateResumeTokenAsync(sessionId, staleToken);

        // Simulate Fleet restart: fresh orchestrator with empty tracker (no live instances).
        var restartedTracker = new InstanceTracker();
        var restartedOrchestrator = CreateOrchestrator(builder, restartedTracker, publisher, pooledOpenCodeHarness: true);

        // Resume resolves the stale token and returns a fresh session.
        var freshToken = "oc-fresh-after-crash";
        var resumedHarness = new FakeHarnessSession("inst-crash-resumed") { ResumeToken = freshToken };
        runtime.ResumeBehavior = (_, _) => Task.FromResult<IHarnessSession>(resumedHarness);

        var promptResult = await restartedOrchestrator.PromptSessionAsync(
            sessionId,
            "hello after crash",
            options: null,
            CancellationToken.None);

        promptResult.IsSuccess.ShouldBeTrue();
        // The resumed harness received the prompt.
        resumedHarness.SendPromptCalls.Count.ShouldBe(1);
        resumedHarness.SendPromptCalls[0].Text.ShouldBe("hello after crash");
        // The resume was called with the stale token (from the DB).
        runtime.ResumeCalls.Count.ShouldBe(1);
        runtime.ResumeCalls[0].ResumeToken.ShouldBe(staleToken);
        // The restarted tracker holds the resumed instance.
        restartedTracker.Get("inst-crash-resumed").ShouldBeSameAs(resumedHarness);
    }

    private static SessionOrchestratorBuilder CreateBuilder()
    {
        var builder = new SessionOrchestratorBuilder()
            .WithUserContext(new TestUserContext("user-1"))
            .WithOptions(new FleetOptions
            {
                Harness = new HarnessOptions
                {
                    PooledOpenCodeHarness = true,
                },
            });

        builder.UserPreferenceRepository.SetAsync("PooledOpenCodeHarness", "true").GetAwaiter().GetResult();
        SeedWorkspaceRoot(builder);
        return builder;
    }

    private static SessionOrchestratorBuilder CreateManualBuilder()
    {
        var builder = new SessionOrchestratorBuilder()
            .WithUserContext(new TestUserContext("user-1"));

        SeedWorkspaceRoot(builder);
        return builder;
    }

    private static void SeedWorkspaceRoot(SessionOrchestratorBuilder builder)
    {
        builder.WorkspaceRootRepository.Seed(new WorkspaceRoot
        {
            Id = "root-1",
            Path = System.IO.Path.GetTempPath(),
            CreatedAt = DateTime.UtcNow.ToString("O"),
        });
    }

    private static SessionOrchestrator CreateOrchestrator(
        SessionOrchestratorBuilder builder,
        InstanceTracker tracker,
        IEventPublisher eventPublisher,
        bool pooledOpenCodeHarness)
    {
        var userContext = new TestUserContext("user-1");
        var options = new FleetOptions
        {
            Harness = new HarnessOptions
            {
                PooledOpenCodeHarness = pooledOpenCodeHarness,
            },
        };
        var workspaceRootService = new WorkspaceRootService(builder.WorkspaceRootRepository, userContext);
        var workspaceService = new WorkspaceService(
            builder.WorkspaceRepository,
            userContext,
            options,
            NullLogger<WorkspaceService>.Instance);
        var instanceService = new InstanceService(builder.InstanceRepository, builder.SessionRepository, userContext);
        var sourceResolutionService = new SessionSourceResolutionService([
            new LocalDirectorySessionSourceProvider(workspaceRootService)
        ]);
        var delegationService = new DelegationService(builder.DelegationRepository, builder.EventBroadcaster, userContext);

        return new SessionOrchestrator(
            workspaceService,
            instanceService,
            sourceResolutionService,
            builder.HarnessRegistry,
            tracker,
            builder.SessionRepository,
            builder.SessionSourceUsageRepository,
            builder.SessionCallbackRepository,
            builder.DelegationRepository,
            builder.ProjectRepository,
            builder.EventBroadcaster,
            eventPublisher,
            builder.AnalyticsCollector,
            builder.MessageRepository,
            builder.HarnessEventLogRepository,
            delegationService,
            builder.CredentialStore,
            builder.UserPreferenceRepository,
            userContext,
            options,
            builder.SmartLinkRepository,
            NullLogger<SessionOrchestrator>.Instance,
            sessionActivityWriteService: null);
    }

    private static HarnessEvent CreateAssistantResponseEvent(string openCodeSessionId, string messageId, string text)
        => new()
        {
            Type = EventTypes.MessageUpdated,
            SessionId = openCodeSessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id = messageId,
                    sessionID = openCodeSessionId,
                    role = "assistant",
                    agent = "loom",
                    time = new { created = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
                },
                parts = new[]
                {
                    new
                    {
                        type = "text",
                        id = $"part-{messageId}",
                        sessionID = openCodeSessionId,
                        messageID = messageId,
                        text
                    }
                }
            })
        };

    private sealed class ProjectingEventPublisher(SessionOrchestratorBuilder builder) : IEventPublisher
    {
        private long _nextEventId;

        public async Task<PublishResult> PublishAsync(HarnessEvent evt, EventPublishContext context, CancellationToken ct)
        {
            var eventId = Interlocked.Increment(ref _nextEventId);
            if (context.UserId is not null && evt.Type != EventTypes.UserPromptCommitted)
            {
                await ProjectAsync(context.FleetSessionId, context.UserId, evt, ct).ConfigureAwait(false);
            }

            return new PublishResult(eventId, IsDuplicate: false);
        }

        public async Task ProjectAsync(string sessionId, string userId, HarnessEvent evt, CancellationToken ct)
        {
            var persister = new RecordingHarnessEventPersister(builder.MessageRepository);
            var projection = new MessagePersistenceProjection(persister, builder.HarnessEventLogRepository);
            await projection.HandleAsync(evt, new ProjectionContext(
                Tenant: "tenant.default",
                ProjectId: "scratch",
                FleetSessionId: sessionId,
                EventType: evt.Type,
                UserId: userId,
                HarnessType: "opencode",
                StreamSequence: Interlocked.Increment(ref _nextEventId),
                InternalPumpDedupKey: 0), ct).ConfigureAwait(false);
        }
    }

    private sealed class RecordingHarnessEventPersister(InMemoryMessageRepository messageRepository) : IHarnessEventPersister
    {
        public Task HandleAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt, CancellationToken ct)
        {
            if (evt.Type != EventTypes.MessageUpdated || !evt.Payload.HasValue)
            {
                return Task.CompletedTask;
            }

            var payload = evt.Payload.Value;
            var info = payload.GetProperty("info");
            if (info.GetProperty("role").GetString() != "assistant")
            {
                return Task.CompletedTask;
            }

            var messageId = info.GetProperty("id").GetString() ?? throw new InvalidOperationException("Assistant response id missing.");
            var partsJson = payload.GetProperty("parts").GetRawText();
            return messageRepository.UpsertAsync(new PersistedMessage
            {
                Id = messageId,
                SessionId = fleetSessionId,
                Role = "assistant",
                PartsJson = partsJson,
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
        }

        public void BufferTextDelta(string fleetSessionId, HarnessEvent evt)
        {
        }

        public Task FlushBufferedDeltasAsync(string fleetSessionId, string ownerUserId, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fleet-auto-activation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
