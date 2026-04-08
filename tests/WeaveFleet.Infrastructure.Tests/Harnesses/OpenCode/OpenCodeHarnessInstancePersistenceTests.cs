using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

/// <summary>
/// Tests that <see cref="OpenCodeHarnessInstance"/> persists messages to the database
/// via <see cref="IMessageRepository"/> when processing SSE events in SubscribeAsync.
/// These tests were relocated from HarnessEventRelayTests after the persistence logic
/// was moved from the relay into the instance (instance-owned persistence pattern).
/// </summary>
public sealed class OpenCodeHarnessInstancePersistenceTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds persistence dependencies backed by a real ServiceCollection so that
    /// GetRequiredService&lt;T&gt;() resolves correctly without NSubstitute IServiceProvider quirks.
    /// </summary>
    private static (IServiceScopeFactory ScopeFactory, IMessageRepository MessageRepo, IDelegationRepository DelegationRepo, IEventBroadcaster EventBroadcaster)
        BuildPersistenceDependencies()
    {
        var messageRepo = Substitute.For<IMessageRepository>();
        var delegationRepo = Substitute.For<IDelegationRepository>();
        var eventBroadcaster = Substitute.For<IEventBroadcaster>();
        var delegationService = new DelegationService(delegationRepo, eventBroadcaster);

        var services = new ServiceCollection();
        services.AddSingleton(messageRepo);
        services.AddSingleton(delegationRepo);
        services.AddSingleton(eventBroadcaster);
        services.AddSingleton(delegationService);
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        return (scopeFactory, messageRepo, delegationRepo, eventBroadcaster);
    }

    /// <summary>
    /// Builds a valid OpenCode message.created event SSE line with the given role and messageId.
    /// </summary>
    private static string BuildMessageCreatedSseLine(string role = "assistant", string messageId = "msg-1") =>
        "data: " + JsonSerializer.Serialize(new
        {
            type = "message.created",
            properties = new
            {
                info = new
                {
                    id = messageId,
                    sessionId = "oc-session",
                    role,
                    time = new { created = 1700000000L }
                },
                parts = new[] { new { type = "text", id = "p1", sessionId = "oc-session", messageId, text = "Hello" } }
            }
        });

    /// <summary>
    /// Builds a valid OpenCode message.part.updated event SSE line.
    /// </summary>
    private static string BuildPartUpdatedSseLine(
        string messageId = "msg-1",
        string sessionId = "oc-session",
        string text = "Hello from part") =>
        "data: " + JsonSerializer.Serialize(new
        {
            type = "message.part.updated",
            properties = new
            {
                part = new
                {
                    id = "part-1",
                    sessionID = sessionId,
                    messageID = messageId,
                    type = "text",
                    text
                }
            }
        });

    /// <summary>
    /// Builds a valid OpenCode message.updated event SSE line.
    /// </summary>
    private static string BuildMessageUpdatedSseLine(string role = "assistant", string messageId = "msg-1") =>
        "data: " + JsonSerializer.Serialize(new
        {
            type = "message.updated",
            properties = new
            {
                info = new
                {
                    id = messageId,
                    sessionId = "oc-session",
                    role,
                    time = new { created = 1700000000L }
                },
                parts = new[] { new { type = "text", id = "p1", sessionId = "oc-session", messageId, text = "Hello" } }
            }
        });

    /// <summary>
    /// Creates an <see cref="OpenCodeHarnessInstance"/> backed by a fake SSE HTTP handler
    /// that streams the provided SSE lines.
    /// </summary>
    private static async Task<(OpenCodeHarnessInstance Instance, IMessageRepository MessageRepo)>
        CreateInstanceWithSseLines(
            string fleetSessionId,
            IEnumerable<string> sseLines,
            Action<IMessageRepository>? configureRepo = null)
    {
        var (scopeFactory, messageRepo, _, _) = BuildPersistenceDependencies();
        configureRepo?.Invoke(messageRepo);

        // Build the SSE response body — each line followed by CRLF, then a blank line
        var sseBody = new StringBuilder();
        foreach (var line in sseLines)
        {
            sseBody.AppendLine(line);
            sseBody.AppendLine(); // blank line to separate SSE events
        }

        var handler = new FakeSseHttpMessageHandler(sseBody.ToString());
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var ocHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);

        var processManager = new OpenCodeProcessManager(NullLogger<OpenCodeProcessManager>.Instance);
        const int allocatedPort = 0; // not used in these tests

        var instance = new OpenCodeHarnessInstance(
            instanceId: "test-instance",
            fleetSessionId: fleetSessionId,
            httpClient: ocHttpClient,
            processManager: processManager,
            portAllocator: new PortAllocator(10000, 10099),
            allocatedPort: allocatedPort,
            workingDirectory: "/tmp",
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessInstance>.Instance);

        return (instance, messageRepo);
    }

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    /// <summary>
    /// Consumes all events from the instance's SubscribeAsync stream until the stream ends
    /// (when the fake HTTP handler closes the connection) or the CTS fires.
    /// OperationCanceledException from the CT is expected and silently ignored.
    /// </summary>
    private static async Task<List<HarnessEvent>> ConsumeEventsAsync(
        OpenCodeHarnessInstance instance,
        CancellationToken ct)
    {
        var events = new List<HarnessEvent>();
        try
        {
            await foreach (var evt in instance.SubscribeAsync(ct))
                events.Add(evt);
        }
        catch (OperationCanceledException)
        {
            // Expected — CTS fired after events were processed
        }
        return events;
    }

    [Fact]
    public async Task SubscribeAsync_MessageUpdatedEvent_DoesNotPersist()
    {
        var fleetSessionId = "fleet-persist-1";
        var eventDeliveredSignal = new TaskCompletionSource();

        var (instance, messageRepo) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [BuildMessageUpdatedSseLine("assistant")]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var capturedEvents = new List<HarnessEvent>();

        // Consume in background — cancel after the first event is delivered
        var consumeTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in instance.SubscribeAsync(cts.Token))
                {
                    capturedEvents.Add(evt);
                    eventDeliveredSignal.TrySetResult();
                }
            }
            catch (OperationCanceledException) { /* expected */ }
        });

        await eventDeliveredSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Give fire-and-forget tasks time to settle before cancelling
        await Task.Delay(200);

        // message.updated must NOT trigger UpsertAsync
        await messageRepo.DidNotReceive().UpsertAsync(Arg.Any<PersistedMessage>());

        // Assert the first delivered event is message.updated
        Assert.NotEmpty(capturedEvents);
        Assert.Equal("message.updated", capturedEvents[0].Type);

        await cts.CancelAsync();
        await consumeTask;

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_MessageCreatedEvent_PersistsMessage()
    {
        var fleetSessionId = "fleet-persist-2";
        var persistSignal = new TaskCompletionSource();

        var (instance, messageRepo) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [BuildMessageCreatedSseLine("user", "msg-user-1")],
            repo =>
            {
                repo.UpsertAsync(Arg.Any<PersistedMessage>()).Returns(callInfo =>
                {
                    persistSignal.TrySetResult();
                    return Task.CompletedTask;
                });
            });

        // Start consuming in background — the stream reconnects, so we cancel after persist fires
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

        // Wait for persist to fire, then cancel the stream
        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await consumeTask;

        await messageRepo.Received(1).UpsertAsync(Arg.Is<PersistedMessage>(m =>
            m.SessionId == fleetSessionId && m.Role == "user"));

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_NonMessageEvent_DoesNotPersist()
    {
        var fleetSessionId = "fleet-persist-3";

        var sessionStatusLine = "data: " + JsonSerializer.Serialize(new
        {
            type = "session.status",
            properties = new { status = "idle" }
        });

        // Track when at least one event is broadcast to know when processing is done
        var (instance, messageRepo) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [sessionStatusLine]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

        // Give the event time to be processed, then cancel
        await Task.Delay(300);
        await cts.CancelAsync();
        await consumeTask;

        await messageRepo.DidNotReceive().UpsertAsync(Arg.Any<PersistedMessage>());

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_PersistenceFailure_DoesNotBlockEventDelivery()
    {
        var fleetSessionId = "fleet-persist-4";
        var eventDeliveredSignal = new TaskCompletionSource();

        var (instance, messageRepo) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [BuildMessageCreatedSseLine("assistant")],
            repo =>
            {
                repo.UpsertAsync(Arg.Any<PersistedMessage>())
                    .Returns(_ => Task.FromException(new InvalidOperationException("DB is on fire")));
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<HarnessEvent>();

        // Collect first event then cancel
        var consumeTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in instance.SubscribeAsync(cts.Token))
                {
                    events.Add(evt);
                    eventDeliveredSignal.TrySetResult();
                }
            }
            catch (OperationCanceledException) { /* expected */ }
        });

        await eventDeliveredSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await consumeTask;

        // Event must be delivered despite DB failure
        Assert.NotEmpty(events);
        Assert.Equal("message.created", events[0].Type);

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_MessagePartUpdated_TextPart_PersistsIncrementally()
    {
        var fleetSessionId = "fleet-part-persist-1";
        var messageId = "msg-part-1";
        var persistSignal = new TaskCompletionSource();

        var (instance, messageRepo) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [BuildPartUpdatedSseLine(messageId, "oc-session", "Hello from part")],
            repo =>
            {
                repo.GetByIdAsync(messageId, fleetSessionId)
                    .Returns(Task.FromResult<PersistedMessage?>(null));

                repo.UpsertAsync(Arg.Any<PersistedMessage>()).Returns(callInfo =>
                {
                    persistSignal.TrySetResult();
                    return Task.CompletedTask;
                });
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await consumeTask;

        await messageRepo.Received(1).UpsertAsync(Arg.Is<PersistedMessage>(m =>
            m.Id == messageId &&
            m.SessionId == fleetSessionId &&
            m.Role == "assistant" &&
            m.PartsJson.Contains("Hello from part")));

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_MessageCreated_EmptyParts_DoesNotOverwriteExisting()
    {
        var fleetSessionId = "fleet-guard-1";
        var messageId = "msg-guard-1";

        var emptyPartsLine = "data: " + JsonSerializer.Serialize(new
        {
            type = "message.created",
            properties = new
            {
                info = new
                {
                    id = messageId,
                    sessionId = "oc-session",
                    role = "assistant",
                    time = new { created = 1700000000L }
                },
                parts = Array.Empty<object>()
            }
        });

        var existingMessage = new PersistedMessage
        {
            Id = messageId,
            SessionId = fleetSessionId,
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"existing content"}]""",
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };

        var (instance, messageRepo) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [emptyPartsLine],
            repo =>
            {
                repo.GetByIdAsync(messageId, fleetSessionId)
                    .Returns(Task.FromResult<PersistedMessage?>(existingMessage));
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

        // Give fire-and-forget tasks time to settle, then cancel
        await Task.Delay(300);
        await cts.CancelAsync();
        await consumeTask;

        // UpsertAsync must NOT be called — guard prevented overwrite
        await messageRepo.DidNotReceive().UpsertAsync(Arg.Any<PersistedMessage>());

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_FullLifecycle_MessageUpdatedDoesNotOverwriteParts()
    {
        var fleetSessionId = "fleet-lifecycle-1";
        var messageId = "msg-lifecycle-1";

        PersistedMessage? lastUpserted = null;
        var persistedMessages = new List<PersistedMessage>();
        var upsertSignal = new TaskCompletionSource();

        var skeletonLine = "data: " + JsonSerializer.Serialize(new
        {
            type = "message.created",
            properties = new
            {
                info = new
                {
                    id = messageId,
                    sessionId = "oc-session",
                    role = "assistant",
                    time = new { created = 1700000000L }
                },
                parts = Array.Empty<object>()
            }
        });

        var (instance, messageRepo) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [
                skeletonLine,
                BuildPartUpdatedSseLine(messageId, "oc-session", "The actual answer"),
                BuildMessageUpdatedSseLine("assistant", messageId)
            ],
            repo =>
            {
                repo.GetByIdAsync(messageId, fleetSessionId)
                    .Returns(callInfo => Task.FromResult(lastUpserted));

                repo.UpsertAsync(Arg.Any<PersistedMessage>()).Returns(callInfo =>
                {
                    lastUpserted = callInfo.ArgAt<PersistedMessage>(0);
                    persistedMessages.Add(lastUpserted);
                    upsertSignal.TrySetResult();
                    return Task.CompletedTask;
                });
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

        await upsertSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        await cts.CancelAsync();
        await consumeTask;

        // Final state must contain the text part (not overwritten by message.updated)
        Assert.NotNull(lastUpserted);
        Assert.Contains("The actual answer", lastUpserted.PartsJson);

        // The final persisted state must not be empty
        Assert.NotEqual("[]", lastUpserted.PartsJson);

        // At least one upsert must have occurred (from message.part.updated)
        Assert.True(persistedMessages.Count >= 1, "Expected at least one UpsertAsync call from part.updated");

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_TaskToolEvent_CreatesDelegationAndBroadcasts()
    {
        var messageRepo = Substitute.For<IMessageRepository>();
        var delegationRepo = Substitute.For<IDelegationRepository>();
        var eventBroadcaster = Substitute.For<IEventBroadcaster>();

        delegationRepo.GetByParentToolCallIdAsync("fleet-delegation-1", "tool-1")
            .Returns((Delegation?)null);

        var services = new ServiceCollection();
        services.AddSingleton(messageRepo);
        services.AddSingleton(delegationRepo);
        services.AddSingleton(eventBroadcaster);
        services.AddSingleton(new DelegationService(delegationRepo, eventBroadcaster));
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        var sseLine = "data: " + JsonSerializer.Serialize(new
        {
            type = "message.part.updated",
            properties = new
            {
                part = new
                {
                    id = "part-1",
                    sessionID = "oc-parent",
                    messageID = "msg-1",
                    type = "tool",
                    tool = "task",
                    callID = "tool-1",
                    state = new
                    {
                        status = "pending",
                        input = new
                        {
                            subagent_type = "reviewer",
                            description = "Review code"
                        }
                    }
                }
            }
        });

        var sseBody = new StringBuilder();
        sseBody.AppendLine(sseLine);
        sseBody.AppendLine();

        var handler = new FakeSseHttpMessageHandler(sseBody.ToString());
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var ocHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);
        var processManager = new OpenCodeProcessManager(NullLogger<OpenCodeProcessManager>.Instance);

        var instance = new OpenCodeHarnessInstance(
            instanceId: "test-instance",
            fleetSessionId: "fleet-delegation-1",
            httpClient: ocHttpClient,
            processManager: processManager,
            portAllocator: new PortAllocator(10000, 10099),
            allocatedPort: 0,
            workingDirectory: "/tmp",
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessInstance>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

        await Task.Delay(300);
        await cts.CancelAsync();
        await consumeTask;

        await delegationRepo.Received(1).InsertAsync(Arg.Is<Delegation>(d =>
            d.ParentSessionId == "fleet-delegation-1" &&
            d.ParentToolCallId == "tool-1" &&
            d.Title == "reviewer" &&
            d.Status == "pending"));

        await eventBroadcaster.Received(1).BroadcastAsync(
            "session:fleet-delegation-1",
            "delegation.created",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_TaskToolRunningWithChildSession_LinksDelegationAndBroadcastsUpdate()
    {
        var messageRepo = Substitute.For<IMessageRepository>();
        var delegationRepo = Substitute.For<IDelegationRepository>();
        var eventBroadcaster = Substitute.For<IEventBroadcaster>();

        delegationRepo.GetByParentToolCallIdAsync("fleet-delegation-1", "tool-1")
            .Returns(
                (Delegation?)null,
                new Delegation
                {
                    Id = "del-1",
                    ParentSessionId = "fleet-delegation-1",
                    ParentToolCallId = "tool-1",
                    Title = "reviewer",
                    Status = "pending",
                    CreatedAt = DateTime.UtcNow.ToString("O"),
                    UpdatedAt = DateTime.UtcNow.ToString("O")
                });

        var sessionRepo = Substitute.For<ISessionRepository>();
        sessionRepo.GetByIdAsync("fleet-delegation-1").Returns(new Session
        {
            Id = "fleet-delegation-1",
            WorkspaceId = "ws-1",
            InstanceId = "inst-parent",
            HarnessType = "opencode",
            Title = "Parent",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });
        sessionRepo.GetByHarnessIdAsync("child-1").Returns((Session?)null);
        var harnessRegistry = Substitute.For<IHarnessRegistry>();
        var harness = Substitute.For<IHarness>();
        harness.Capabilities.Returns(new HarnessCapabilities { SupportsResume = true });
        var childHarnessInstance = Substitute.For<IHarnessInstance>();
        childHarnessInstance.InstanceId.Returns("inst-child");
        childHarnessInstance.HarnessType.Returns("opencode");
        childHarnessInstance.Status.Returns(HarnessInstanceStatus.Running);
        harness.ResumeAsync(Arg.Any<HarnessResumeOptions>(), Arg.Any<CancellationToken>()).Returns(childHarnessInstance);
        harnessRegistry.GetByType("opencode").Returns(harness);
        var instanceRepo = Substitute.For<IInstanceRepository>();
        instanceRepo.InsertAsync(Arg.Any<Instance>()).Returns(Task.CompletedTask);
        var projectRepo = Substitute.For<IProjectRepository>();
        projectRepo.ListAsync().Returns(new List<Project>());
        var callbackRepo = Substitute.For<ISessionCallbackRepository>();
        var workspaceRepo = Substitute.For<IWorkspaceRepository>();
        var analyticsCollector = Substitute.For<IAnalyticsCollector>();

        var services = new ServiceCollection();
        services.AddSingleton(messageRepo);
        services.AddSingleton(delegationRepo);
        services.AddSingleton(eventBroadcaster);
        services.AddSingleton(sessionRepo);
        services.AddSingleton(instanceRepo);
        services.AddSingleton(projectRepo);
        services.AddSingleton(callbackRepo);
        services.AddSingleton(workspaceRepo);
        services.AddSingleton<IHarnessRegistry>(harnessRegistry);
        services.AddSingleton(analyticsCollector);
        services.AddSingleton(new InstanceTracker());
        services.AddSingleton(new InstanceService(instanceRepo, sessionRepo));
        services.AddSingleton(new DelegationService(delegationRepo, eventBroadcaster));
        services.AddSingleton(new SessionOrchestrator(
            new WorkspaceService(workspaceRepo, NullLogger<WorkspaceService>.Instance),
            new InstanceService(instanceRepo, sessionRepo),
            harnessRegistry,
            new InstanceTracker(),
            sessionRepo,
            callbackRepo,
            delegationRepo,
            projectRepo,
            eventBroadcaster,
            analyticsCollector,
            messageRepo,
            new DelegationService(delegationRepo, eventBroadcaster),
            new FleetOptions(),
            NullLogger<SessionOrchestrator>.Instance));
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        var pendingLine = "data: " + JsonSerializer.Serialize(new
        {
            type = "message.part.updated",
            properties = new
            {
                part = new
                {
                    id = "part-1",
                    sessionID = "oc-parent",
                    messageID = "msg-1",
                    type = "tool",
                    tool = "task",
                    callID = "tool-1",
                    state = new
                    {
                        status = "pending",
                        input = new
                        {
                            subagent_type = "reviewer",
                            description = "Review code"
                        }
                    }
                }
            }
        });

        var runningLine = "data: " + JsonSerializer.Serialize(new
        {
            type = "message.part.updated",
            properties = new
            {
                part = new
                {
                    id = "part-1",
                    sessionID = "oc-parent",
                    messageID = "msg-1",
                    type = "tool",
                    tool = "task",
                    callID = "tool-1",
                    state = new
                    {
                        status = "running",
                        input = new
                        {
                            subagent_type = "reviewer"
                        },
                        metadata = new
                        {
                            child = new
                            {
                                sessionId = "child-1"
                            }
                        }
                    }
                }
            }
        });

        var sseBody = new StringBuilder();
        sseBody.AppendLine(pendingLine);
        sseBody.AppendLine();
        sseBody.AppendLine(runningLine);
        sseBody.AppendLine();

        var handler = new FakeSseHttpMessageHandler(sseBody.ToString());
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var ocHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);
        var processManager = new OpenCodeProcessManager(NullLogger<OpenCodeProcessManager>.Instance);

        var instance = new OpenCodeHarnessInstance(
            instanceId: "test-instance",
            fleetSessionId: "fleet-delegation-1",
            httpClient: ocHttpClient,
            processManager: processManager,
            portAllocator: new PortAllocator(10000, 10099),
            allocatedPort: 0,
            workingDirectory: "/tmp",
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessInstance>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

        await Task.Delay(300);
        await cts.CancelAsync();
        await consumeTask;

        await delegationRepo.Received(1).UpdateChildSessionIdAsync(
            "del-1",
            Arg.Any<string>(),
            Arg.Any<string>());

        await eventBroadcaster.Received().BroadcastAsync(
            "session:fleet-delegation-1",
            "delegation.updated",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_SubtaskPartWithChildSession_LinksDelegationAndBroadcastsUpdate()
    {
        var messageRepo = Substitute.For<IMessageRepository>();
        var delegationRepo = Substitute.For<IDelegationRepository>();
        var eventBroadcaster = Substitute.For<IEventBroadcaster>();
        var sessionRepo = Substitute.For<ISessionRepository>();
        sessionRepo.GetByIdAsync("fleet-delegation-1").Returns(new Session
        {
            Id = "fleet-delegation-1",
            WorkspaceId = "ws-1",
            InstanceId = "inst-parent",
            HarnessType = "opencode",
            Title = "Parent",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });
        sessionRepo.GetByHarnessIdAsync("child-1").Returns((Session?)null);
        var harnessRegistry = Substitute.For<IHarnessRegistry>();
        var harness = Substitute.For<IHarness>();
        harness.Capabilities.Returns(new HarnessCapabilities { SupportsResume = true });
        var childHarnessInstance = Substitute.For<IHarnessInstance>();
        childHarnessInstance.InstanceId.Returns("inst-child");
        childHarnessInstance.HarnessType.Returns("opencode");
        childHarnessInstance.Status.Returns(HarnessInstanceStatus.Running);
        harness.ResumeAsync(Arg.Any<HarnessResumeOptions>(), Arg.Any<CancellationToken>()).Returns(childHarnessInstance);
        harnessRegistry.GetByType("opencode").Returns(harness);
        var instanceRepo = Substitute.For<IInstanceRepository>();
        instanceRepo.InsertAsync(Arg.Any<Instance>()).Returns(Task.CompletedTask);
        var projectRepo = Substitute.For<IProjectRepository>();
        projectRepo.ListAsync().Returns(new List<Project>());
        var callbackRepo = Substitute.For<ISessionCallbackRepository>();
        var workspaceRepo = Substitute.For<IWorkspaceRepository>();
        var analyticsCollector = Substitute.For<IAnalyticsCollector>();

        delegationRepo.GetByParentToolCallIdAsync("fleet-delegation-1", "tool-1")
            .Returns(
                new Delegation
                {
                    Id = "del-1",
                    ParentSessionId = "fleet-delegation-1",
                    ParentToolCallId = "tool-1",
                    Title = "thread",
                    Status = "pending",
                    CreatedAt = DateTime.UtcNow.ToString("O"),
                    UpdatedAt = DateTime.UtcNow.ToString("O")
                });

        var services = new ServiceCollection();
        services.AddSingleton(messageRepo);
        services.AddSingleton(delegationRepo);
        services.AddSingleton(eventBroadcaster);
        services.AddSingleton(sessionRepo);
        services.AddSingleton(instanceRepo);
        services.AddSingleton(projectRepo);
        services.AddSingleton(callbackRepo);
        services.AddSingleton(workspaceRepo);
        services.AddSingleton<IHarnessRegistry>(harnessRegistry);
        services.AddSingleton(analyticsCollector);
        services.AddSingleton(new InstanceTracker());
        services.AddSingleton(new InstanceService(instanceRepo, sessionRepo));
        services.AddSingleton(new DelegationService(delegationRepo, eventBroadcaster));
        services.AddSingleton(new SessionOrchestrator(
            new WorkspaceService(workspaceRepo, NullLogger<WorkspaceService>.Instance),
            new InstanceService(instanceRepo, sessionRepo),
            harnessRegistry,
            new InstanceTracker(),
            sessionRepo,
            callbackRepo,
            delegationRepo,
            projectRepo,
            eventBroadcaster,
            analyticsCollector,
            messageRepo,
            new DelegationService(delegationRepo, eventBroadcaster),
            new FleetOptions(),
            NullLogger<SessionOrchestrator>.Instance));
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        var subtaskLine = "data: " + JsonSerializer.Serialize(new
        {
            type = "message.part.updated",
            properties = new
            {
                part = new
                {
                    id = "subtask-1",
                    sessionID = "oc-parent",
                    messageID = "msg-1",
                    type = "subtask",
                    callId = "tool-1",
                    agent = "thread",
                    description = "Investigate issue",
                    metadata = new
                    {
                        child = new
                        {
                            sessionId = "child-1"
                        }
                    }
                }
            }
        });

        var sseBody = new StringBuilder();
        sseBody.AppendLine(subtaskLine);
        sseBody.AppendLine();

        var handler = new FakeSseHttpMessageHandler(sseBody.ToString());
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var ocHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);
        var processManager = new OpenCodeProcessManager(NullLogger<OpenCodeProcessManager>.Instance);

        var instance = new OpenCodeHarnessInstance(
            instanceId: "test-instance",
            fleetSessionId: "fleet-delegation-1",
            httpClient: ocHttpClient,
            processManager: processManager,
            portAllocator: new PortAllocator(10000, 10099),
            allocatedPort: 0,
            workingDirectory: "/tmp",
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessInstance>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

        await Task.Delay(300);
        await cts.CancelAsync();
        await consumeTask;

        await delegationRepo.Received(1).UpdateChildSessionIdAsync(
            "del-1",
            Arg.Any<string>(),
            Arg.Any<string>());

        await eventBroadcaster.Received().BroadcastAsync(
            "session:fleet-delegation-1",
            "delegation.updated",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_RoutesEventsFromDelegatedChildOpenCodeSession()
    {
        var fleetSessionId = "fleet-parent";
        var messageRepo = Substitute.For<IMessageRepository>();
        var delegationRepo = Substitute.For<IDelegationRepository>();
        var eventBroadcaster = Substitute.For<IEventBroadcaster>();
        var sessionRepo = Substitute.For<ISessionRepository>();
        sessionRepo.GetByHarnessIdAsync("oc-child").Returns(new Session
        {
            Id = "fleet-child",
            ParentSessionId = "fleet-parent",
            InstanceId = "inst-child"
        });

        var services = new ServiceCollection();
        services.AddSingleton(messageRepo);
        services.AddSingleton(delegationRepo);
        services.AddSingleton(eventBroadcaster);
        services.AddSingleton(sessionRepo);
        services.AddSingleton(new DelegationService(delegationRepo, eventBroadcaster));
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        var childEventLine = BuildPartUpdatedSseLine("msg-child", "oc-child", "child text");
        var sseBody = new StringBuilder();
        sseBody.AppendLine(childEventLine);
        sseBody.AppendLine();

        var handler = new FakeSseHttpMessageHandler(sseBody.ToString());
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var ocHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);
        var processManager = new OpenCodeProcessManager(NullLogger<OpenCodeProcessManager>.Instance);

        var instance = new OpenCodeHarnessInstance(
            instanceId: "test-instance",
            fleetSessionId: fleetSessionId,
            httpClient: ocHttpClient,
            processManager: processManager,
            portAllocator: new PortAllocator(10000, 10099),
            allocatedPort: 0,
            workingDirectory: "/tmp",
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessInstance>.Instance,
            openCodeSessionId: "oc-parent");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);
        await Task.Delay(300);
        await cts.CancelAsync();
        var events = await consumeTask;

        var routed = Assert.Single(events);
        Assert.Equal("oc-child", routed.SessionId);
        Assert.Equal("fleet-child", routed.FleetSessionId);
        await messageRepo.DidNotReceive().UpsertAsync(Arg.Any<PersistedMessage>());

        await instance.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // Test double: fake HTTP message handler that streams SSE data
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns a single HTTP response containing the provided SSE body, then closes the stream.
    /// </summary>
    private sealed class FakeSseHttpMessageHandler(string sseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            return Task.FromResult(response);
        }
    }
}
