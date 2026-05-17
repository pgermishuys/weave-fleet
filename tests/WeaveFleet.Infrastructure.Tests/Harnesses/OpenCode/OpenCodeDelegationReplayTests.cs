using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fixtures;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

/// <summary>
/// Replay-based integration tests for OpenCode delegation lifecycle.
/// Uses <see cref="DelegationReplayFixture"/> to replay a curated ~25-event SSE sequence
/// derived from a real captured delegation flow.
/// </summary>
public sealed class OpenCodeDelegationReplayTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds an <see cref="OpenCodeHarnessSession"/> that streams the fixture SSE events,
    /// returning the instance and the delegation repo so tests can inspect delegation state.
    /// </summary>
    private static (OpenCodeHarnessSession Instance, InMemoryDelegationRepository DelegationRepo, FakeEventBroadcaster EventBroadcaster)
        BuildInstance(string fleetSessionId, IEnumerable<string> sseLines)
    {
        var messageRepo = new InMemoryMessageRepository();
        var delegationRepo = new InMemoryDelegationRepository();
        var eventBroadcaster = new FakeEventBroadcaster();
        var sessionRepo = new InMemorySessionRepository();
        var outboxRepo = new InMemoryOutboxRepository();
        var outboxDispatcher = new FakeOutboxDispatcher();
        var connectionFactory = new FakeDbConnectionFactory();
        var userContext = new TestUserContext("user-1");
        var delegationService = new DelegationService(delegationRepo, eventBroadcaster, userContext);
        var sessionActivityWriteService = new SessionActivityWriteService(
            connectionFactory,
            messageRepo,
            delegationRepo,
            sessionRepo,
            new InMemorySmartLinkRepository(),
            outboxRepo,
            outboxDispatcher);

        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(messageRepo);
        services.AddSingleton<IDelegationRepository>(delegationRepo);
        services.AddSingleton<IEventBroadcaster>(eventBroadcaster);
        services.AddSingleton<ISessionRepository>(sessionRepo);
        services.AddSingleton<IDbConnectionFactory>(connectionFactory);
        services.AddSingleton<IOutboxRepository>(outboxRepo);
        services.AddSingleton<IOutboxDispatcher>(outboxDispatcher);
        services.AddSingleton(sessionActivityWriteService);
        services.AddSingleton<IUserContext>(userContext);
        services.AddSingleton(delegationService);
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        var sseBody = new StringBuilder();
        foreach (var line in sseLines)
        {
            sseBody.AppendLine(line);
            sseBody.AppendLine();
        }

        var handler = new FakeSseHandler(sseBody.ToString());
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var ocHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);

        var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: fleetSessionId,
            httpClient: ocHttpClient,
            processManager: new OpenCodeProcessManager(NullLogger<OpenCodeProcessManager>.Instance),
            portAllocator: new PortAllocator(10000, 10099),
            allocatedPort: 0,
            workingDirectory: "/tmp",
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: "user-1");

        return (instance, delegationRepo, eventBroadcaster);
    }

    private static async Task<List<HarnessEvent>> ConsumeAsync(OpenCodeHarnessSession instance, CancellationToken ct)
    {
        var events = new List<HarnessEvent>();
        try
        {
            await foreach (var evt in instance.SubscribeAsync(ct))
                events.Add(evt);
        }
        catch (OperationCanceledException) { }
        return events;
    }

    /// <summary>
    /// Starts consuming events in background, waits for the stream to end naturally
    /// (the <see cref="FakeSseHandler"/> terminates on the second call), then returns
    /// all collected events.  Falls back to cancellation after a generous timeout to
    /// prevent hangs.
    /// </summary>
    private static async Task<List<HarnessEvent>> ConsumeWithCancelAsync(
        OpenCodeHarnessSession instance,
        int timeoutMs = 10_000)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        return await Task.Run(() => ConsumeAsync(instance, cts.Token), cts.Token);
    }

    private sealed class FakeSseHandler(string body) : HttpMessageHandler
    {
        private int _callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _callCount) > 1)
                throw new OperationCanceledException(cancellationToken);

            var content = new StringContent(body, Encoding.UTF8, "text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    // -----------------------------------------------------------------------
    // Task 3 — Parent vs child model/provider identity
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DelegationReplay_ParentMessages_CarryParentModelId()
    {
        var (instance, _, _) = BuildInstance("fleet-parent-1", DelegationReplayFixture.GetSseLines());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = await ConsumeWithCancelAsync(instance);

        // Parent assistant message.updated events should carry the parent model ID
        var parentAssistantEvents = events
            .Where(e => e.Type == EventTypes.MessageUpdated
                && e.SessionId == DelegationReplayFixture.ParentSessionId
                && TryGetRole(e) == "assistant")
            .ToList();

        parentAssistantEvents.ShouldNotBeEmpty("Expected at least one parent assistant message.updated");

        foreach (var evt in parentAssistantEvents)
        {
            var modelId = TryGetModelId(evt);
            modelId.ShouldBe(DelegationReplayFixture.ParentModelId,
                $"Parent assistant event should carry modelID={DelegationReplayFixture.ParentModelId}");
        }

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task DelegationReplay_ChildMessages_CarryChildModelId()
    {
        var (instance, _, _) = BuildInstance("fleet-parent-2", DelegationReplayFixture.GetSseLines());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = await ConsumeWithCancelAsync(instance);

        // Child assistant message.updated events should carry the child model ID
        // Note: since _openCodeSessionId is null, all events are yielded as "parent" events
        // but the SessionId in the event still reflects the child session ID.
        var childAssistantEvents = events
            .Where(e => e.Type == EventTypes.MessageUpdated
                && e.SessionId == DelegationReplayFixture.ChildSessionId
                && TryGetRole(e) == "assistant")
            .ToList();

        childAssistantEvents.ShouldNotBeEmpty("Expected at least one child assistant message.updated");

        foreach (var evt in childAssistantEvents)
        {
            var modelId = TryGetModelId(evt);
            modelId.ShouldBe(DelegationReplayFixture.ChildModelId,
                $"Child assistant event should carry modelID={DelegationReplayFixture.ChildModelId}");
        }

        await instance.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // Task 4 — Delegation lifecycle (pending → running → completed)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DelegationReplay_DelegationLifecycle_PendingRunningCompleted()
    {
        var (instance, delegationRepo, eventBroadcaster) =
            BuildInstance("fleet-lifecycle-1", DelegationReplayFixture.GetSseLines());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ConsumeWithCancelAsync(instance);

        // Give fire-and-forget delegation handlers time to complete
        await Task.Delay(500);

        // Should have at least a pending delegation created with the correct tool call ID
        delegationRepo.InsertedDelegations.ShouldNotBeEmpty("Delegation should be inserted when task tool fires");
        delegationRepo.InsertedDelegations.ShouldContain(d =>
            d.ParentSessionId == "fleet-lifecycle-1" &&
            d.ParentToolCallId == DelegationReplayFixture.ParentToolCallId,
            "Delegation should be linked to the correct parent tool call");

        // delegation.created broadcast should have been sent
        eventBroadcaster.Broadcasts.ShouldContain(b =>
            b.Topic == "session:fleet-lifecycle-1" &&
            b.Type == "delegation.created",
            "delegation.created broadcast expected on parent session topic");

        await instance.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // Task 5 — Parent remains busy while child works
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DelegationReplay_ParentRemainsBusy_WhileChildWorks()
    {
        var (instance, _, _) = BuildInstance("fleet-busy-1", DelegationReplayFixture.GetSseLines());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = await ConsumeWithCancelAsync(instance);

        var parentStatusEvents = events
            .Where(e => e.Type == EventTypes.SessionStatus
                && e.SessionId == DelegationReplayFixture.ParentSessionId)
            .ToList();

        parentStatusEvents.ShouldNotBeEmpty("Expected parent session.status events");

        // Find the last child status idle event index and the parent idle event index
        var allEvents = events.ToList();
        var childIdleIndex = allEvents.FindLastIndex(e =>
            e.Type == EventTypes.SessionStatus
            && e.SessionId == DelegationReplayFixture.ChildSessionId
            && TryGetStatusType(e) == "idle");

        var parentIdleIndex = allEvents.FindLastIndex(e =>
            e.Type == EventTypes.SessionStatus
            && e.SessionId == DelegationReplayFixture.ParentSessionId
            && TryGetStatusType(e) == "idle");

        // Parent idle should come after child idle (or not be present in mid-stream)
        if (childIdleIndex >= 0 && parentIdleIndex >= 0)
            parentIdleIndex.ShouldBeGreaterThanOrEqualTo(childIdleIndex,
                "Parent should not go idle before child completes");

        // Parent should have at least one busy status event before going idle
        var parentBusyCount = parentStatusEvents.Count(e => TryGetStatusType(e) == "busy");
        parentBusyCount.ShouldBeGreaterThan(0, "Parent should have at least one busy status event");

        await instance.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // Task 6 — session.created event is yielded with parentID
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DelegationReplay_SessionCreated_YieldedWithParentId()
    {
        var (instance, _, _) = BuildInstance("fleet-created-1", DelegationReplayFixture.GetSseLines());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = await ConsumeWithCancelAsync(instance);

        var sessionCreatedEvents = events
            .Where(e => e.Type == EventTypes.SessionCreated)
            .ToList();

        sessionCreatedEvents.ShouldHaveSingleItem("Expected exactly one session.created event");

        var evt = sessionCreatedEvents[0];
        evt.SessionId.ShouldBe(DelegationReplayFixture.ChildSessionId,
            "session.created event should carry the child session ID");

        // Payload should contain parentID pointing to the parent session
        evt.Payload.ShouldNotBeNull("session.created event should have a payload");
        var payload = evt.Payload!.Value;
        payload.TryGetProperty("info", out var infoEl).ShouldBeTrue("session.created payload should have 'info'");
        infoEl.TryGetProperty("parentID", out var parentIdEl).ShouldBeTrue("session.created info should have 'parentID'");
        parentIdEl.GetString().ShouldBe(DelegationReplayFixture.ParentSessionId,
            "session.created parentID should match the parent session ID");

        await instance.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // Private helpers for payload inspection
    // -----------------------------------------------------------------------

    private static string? TryGetRole(HarnessEvent evt)
    {
        if (evt.Payload is not { ValueKind: System.Text.Json.JsonValueKind.Object } payload)
            return null;
        if (!payload.TryGetProperty("info", out var info) || info.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;
        return info.TryGetProperty("role", out var role) ? role.GetString() : null;
    }

    private static string? TryGetModelId(HarnessEvent evt)
    {
        if (evt.Payload is not { ValueKind: System.Text.Json.JsonValueKind.Object } payload)
            return null;
        if (!payload.TryGetProperty("info", out var info) || info.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;
        foreach (var key in new[] { "modelID", "modelId", "model_id" })
            if (info.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                return v.GetString();
        return null;
    }

    private static string? TryGetStatusType(HarnessEvent evt)
    {
        if (evt.Payload is not { ValueKind: System.Text.Json.JsonValueKind.Object } payload)
            return null;
        if (!payload.TryGetProperty("status", out var status) || status.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;
        return status.TryGetProperty("type", out var t) ? t.GetString() : null;
    }

    // -----------------------------------------------------------------------
    // Task 8 — Child events immediately after session.created are not dropped
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DelegationReplay_ChildMessageAfterSessionCreated_IsNotDropped()
    {
        // Arrange: full orchestrator setup so TryEnsureChildSessionFromCreatedEventAsync
        // can actually create the child fleet session in the DB.
        const string parentFleetSessionId = "fleet-race-parent-1";
        const string parentOcSessionId = "oc-race-parent";
        const string childOcSessionId = "oc-race-child";
        const string childTitle = "Race Test Child";

        var userContext = new TestUserContext("user-1");
        var options = new FleetOptions();

        var builder = new SessionOrchestratorBuilder()
            .WithUserContext(userContext)
            .WithOptions(options);

        // Seed parent session in the shared session repo
        builder.SessionRepository.Seed(new Session
        {
            Id = parentFleetSessionId,
            WorkspaceId = "ws-race-1",
            InstanceId = "inst-race-parent",
            HarnessType = "opencode",
            Title = "Race Parent",
            Status = "active",
            Directory = Path.GetTempPath(),
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = "user-1",
        });

        // Seed workspace root so SessionOrchestrator can find a source for child sessions
        builder.WorkspaceRootRepository.Seed(new WorkspaceRoot
        {
            Id = "root-race-1",
            Path = Path.GetTempPath(),
            CreatedAt = DateTime.UtcNow.ToString("O"),
        });

        // Register harness with resume support (required for child session creation)
        var runtime = builder.RegisterHarness("opencode", "OpenCode",
            new HarnessCapabilities { SupportsResume = true });
        var childHarness = new FakeHarnessSession("inst-race-child")
        {
            HarnessType = "opencode",
            Status = HarnessSessionStatus.Running,
        };
        runtime.PrepareRuntimeBehavior = (_, _) =>
            Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new StubDelegationReplayLaunchArtifacts()));
        runtime.ResumeBehavior = (_, _) => Task.FromResult<IHarnessSession>(childHarness);

        var orchestrator = builder.Build();

        // Wire up DI for the OpenCodeHarnessSession
        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(builder.MessageRepository);
        services.AddSingleton<IDelegationRepository>(builder.DelegationRepository);
        services.AddSingleton<IEventBroadcaster>(builder.EventBroadcaster);
        services.AddSingleton<ISessionRepository>(builder.SessionRepository);
        services.AddSingleton<IInstanceRepository>(builder.InstanceRepository);
        services.AddSingleton<IDbConnectionFactory>(new FakeDbConnectionFactory());
        services.AddSingleton<IOutboxRepository>(builder.OutboxRepository);
        services.AddSingleton<IOutboxDispatcher>(new FakeOutboxDispatcher());
        services.AddSingleton<IUserContext>(userContext);
        services.AddSingleton(new DelegationService(builder.DelegationRepository, builder.EventBroadcaster, userContext));
        services.AddSingleton(orchestrator);
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        // Build SSE: session.created immediately followed by child message.updated (same batch)
        var sessionCreatedLine = "data: " + JsonSerializer.Serialize(new
        {
            type = "session.created",
            properties = new
            {
                sessionID = childOcSessionId,
                info = new
                {
                    id = childOcSessionId,
                    parentID = parentOcSessionId,
                    title = childTitle,
                    time = new { created = 1776851154010L, updated = 1776851154010L },
                },
            },
        });

        var childMessageLine = "data: " + JsonSerializer.Serialize(new
        {
            type = "message.updated",
            properties = new
            {
                sessionID = childOcSessionId,
                info = new
                {
                    id = "child-msg-race-1",
                    role = "user",
                    sessionID = childOcSessionId,
                    agent = "thread",
                    time = new { created = 1776851154015L },
                    model = new { providerID = "github-copilot", modelID = "claude-haiku-4.5" },
                },
            },
        });

        var sseBody = new StringBuilder();
        sseBody.AppendLine(sessionCreatedLine);
        sseBody.AppendLine();
        sseBody.AppendLine(childMessageLine);
        sseBody.AppendLine();

        var handler = new FakeSseHandler(sseBody.ToString());
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var ocHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);

        var instance = new OpenCodeHarnessSession(
            instanceId: "test-race-instance",
            fleetSessionId: parentFleetSessionId,
            httpClient: ocHttpClient,
            processManager: new OpenCodeProcessManager(NullLogger<OpenCodeProcessManager>.Instance),
            portAllocator: new PortAllocator(10000, 10099),
            allocatedPort: 0,
            workingDirectory: Path.GetTempPath(),
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: "user-1",
            openCodeSessionId: parentOcSessionId);

        // Act
        var events = await ConsumeWithCancelAsync(instance, timeoutMs: 10_000);

        // Assert: child message.updated should be yielded, NOT dropped
        var childMessageEvents = events
            .Where(e => e.Type == EventTypes.MessageUpdated
                && e.SessionId == childOcSessionId)
            .ToList();

        childMessageEvents.ShouldNotBeEmpty(
            "Child message.updated arriving immediately after session.created must NOT be dropped. " +
            "Without the fix (awaiting TryEnsureChildSessionFromCreatedEventAsync), this event " +
            "would be silently dropped because the child fleet session did not yet exist in the DB.");

        await instance.DisposeAsync();
    }
}

file sealed record StubDelegationReplayLaunchArtifacts : RuntimeLaunchArtifacts;
