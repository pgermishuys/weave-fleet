using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

/// <summary>
/// Tests for harness event persistence. Persistence tests target
/// <see cref="HarnessEventPersistenceService"/> directly (the service that owns
/// durable persistence in the relay pump). Delegation tests target
/// <see cref="OpenCodeHarnessSession.SubscribeAsync"/> which still handles
/// delegation detection as a fire-and-forget side effect.
/// </summary>
public sealed class OpenCodeHarnessSessionPersistenceTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static TestInstanceHandle CreateOwnedHandle(OpenCodeHttpClient httpClient, string workingDirectory) =>
        new TestInstanceHandle(httpClient, workingDirectory);

    private static TestInstanceHandle CreateStartedOwnedHandle(OpenCodeHttpClient httpClient, string workingDirectory) =>
        new TestInstanceHandle(httpClient, workingDirectory);

    private sealed class TestInstanceHandle(OpenCodeHttpClient httpClient, string workingDirectory) : IOpenCodeInstanceHandle
    {
        public event EventHandler<int>? ProcessExited;

        public OpenCodeHttpClient HttpClient { get; } = httpClient;

        public int? ProcessId => 123;

        public bool IsRunning => true;

        public Task EnsureConnectedAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task WaitForEventSubscriptionAsync(string openCodeSessionId, CancellationToken ct) => Task.CompletedTask;

        public Task SendCommandAsync(string openCodeSessionId, OpenCodeCommandRequest request, CancellationToken ct) =>
            HttpClient.SendCommandAsync(openCodeSessionId, request, workingDirectory, ct);

        public IAsyncEnumerable<OpenCodeSseEvent> SubscribeEvents(string? openCodeSessionId, CancellationToken ct) =>
            HttpClient.SubscribeToEventsAsync(workingDirectory, ct);

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _ = ProcessExited;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Builds persistence dependencies backed by a real ServiceCollection so that
    /// GetRequiredService&lt;T&gt;() resolves correctly.
    /// </summary>
    private static (IServiceScopeFactory ScopeFactory, InMemoryMessageRepository MessageRepo, InMemoryDelegationRepository DelegationRepo, FakeEventBroadcaster EventBroadcaster, InMemoryOutboxRepository OutboxRepository)
        BuildPersistenceDependencies()
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

        return (scopeFactory, messageRepo, delegationRepo, eventBroadcaster, outboxRepo);
    }

    /// <summary>
    /// Builds a <see cref="HarnessEventPersistenceService"/> with configurable fake repositories.
    /// </summary>
    private static (HarnessEventPersistenceService Service, InMemoryMessageRepository MessageRepo, InMemoryOutboxRepository OutboxRepo)
        BuildPersistenceService(
            Action<InMemoryMessageRepository>? configureMessageRepo = null,
            Action<InMemorySessionRepository>? configureSessionRepo = null)
    {
        var messageRepo = new InMemoryMessageRepository();
        var sessionRepo = new InMemorySessionRepository();
        var delegationRepo = new InMemoryDelegationRepository();
        var outboxRepo = new InMemoryOutboxRepository();
        var outboxDispatcher = new FakeOutboxDispatcher();
        var connectionFactory = new FakeDbConnectionFactory();

        configureMessageRepo?.Invoke(messageRepo);
        configureSessionRepo?.Invoke(sessionRepo);

        var sessionActivityWriteService = new SessionActivityWriteService(
            connectionFactory,
            messageRepo,
            delegationRepo,
            sessionRepo,
            new InMemorySmartLinkRepository(),
            outboxRepo,
            outboxDispatcher);

        var service = new HarnessEventPersistenceService(
            messageRepo,
            sessionRepo,
            sessionActivityWriteService,
            ownerUserId: "user-1");

        return (service, messageRepo, outboxRepo);
    }

    /// <summary>
    /// Builds a <see cref="HarnessEvent"/> with the given type and JSON payload.
    /// </summary>
    private static HarnessEvent BuildEvent(string type, object payload) =>
        new()
        {
            Type = type,
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload)
        };
    private static string BuildMessageCreatedSseLine(string role, string messageId) =>
        BuildMessageCreatedSseLine(role, messageId, agent: null);

    private static string BuildMessageCreatedSseLine(string role, string messageId, string? agent) =>
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
                    agent,
                    time = new { created = 1700000000L }
                },
                parts = new[] { new { type = "text", id = "p1", sessionId = "oc-session", messageId, text = "Hello" } }
            }
        });

    /// <summary>
    /// Builds a valid OpenCode message.part.updated event SSE line.
    /// </summary>
    private static string BuildPartUpdatedSseLine(string messageId, string sessionId, string text) =>
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

    private static string BuildPartUpdatedSseLine(
        string messageId,
        string sessionId,
        string text,
        string role,
        string? agent) =>
        "data: " + JsonSerializer.Serialize(new
        {
            type = "message.part.updated",
            properties = new
            {
                info = new
                {
                    id = messageId,
                    sessionId,
                    role,
                    agent,
                    time = new { created = 1700000000L }
                },
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

    private static string BuildReasoningPartUpdatedSseLine(string messageId, string sessionId, string partId, string text) =>
        "data: " + JsonSerializer.Serialize(new
        {
            type = "message.part.updated",
            properties = new
            {
                part = new
                {
                    id = partId,
                    sessionID = sessionId,
                    messageID = messageId,
                    type = "reasoning",
                    text
                }
            }
        });

    private static string BuildPartDeltaSseLine(string messageId, string sessionId, string partId, string delta) =>
        "data: " + JsonSerializer.Serialize(new
        {
            type = "message.part.delta",
            properties = new
            {
                sessionID = sessionId,
                messageID = messageId,
                partID = partId,
                field = "text",
                delta,
            }
        });

    private static string BuildFilePartUpdatedSseLine(string messageId, string sessionId, string partId, string mime, string filename, string url) =>
        "data: " + JsonSerializer.Serialize(new
        {
            type = "message.part.updated",
            properties = new
            {
                part = new
                {
                    id = partId,
                    sessionID = sessionId,
                    messageID = messageId,
                    type = "file",
                    mime,
                    filename,
                    url,
                }
            }
        });

    private static string BuildStepFinishPartUpdatedSseLine(string messageId, string sessionId, string partId, int index, string reason) =>
        "data: " + JsonSerializer.Serialize(new
        {
            type = "message.part.updated",
            properties = new
            {
                part = new
                {
                    id = partId,
                    sessionID = sessionId,
                    messageID = messageId,
                    type = "step-finish",
                    index,
                    reason,
                }
            }
        });

    /// <summary>
    /// Builds a valid OpenCode message.updated event SSE line.
    /// </summary>
    private static string BuildMessageUpdatedSseLine(string role, string messageId) =>
        BuildMessageUpdatedSseLine(role, messageId, agent: null);

    private static string BuildMessageUpdatedSseLine(string role, string messageId, string? agent) =>
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
                    agent,
                    time = new { created = 1700000000L }
                },
                parts = new[] { new { type = "text", id = "p1", sessionId = "oc-session", messageId, text = "Hello" } }
            }
        });

    private static string BuildAssistantMessageUpdatedSseLineWithUppercaseModelIds(
        string messageId,
        string modelId,
        string providerId) =>
        "data: " + JsonSerializer.Serialize(new
        {
            type = "message.updated",
            properties = new
            {
                info = new
                {
                    id = messageId,
                    sessionID = "oc-session",
                    role = "assistant",
                    agent = "loom",
                    modelID = modelId,
                    providerID = providerId,
                    time = new { created = 1700000000L }
                },
                parts = new[] { new { type = "text", id = "p1", sessionID = "oc-session", messageID = messageId, text = "Hello" } }
            }
        });

    /// <summary>
    /// Creates an <see cref="OpenCodeHarnessSession"/> backed by a fake SSE HTTP handler
    /// that streams the provided SSE lines. Also returns a <see cref="HarnessEventPersistenceService"/>
    /// pre-wired to the same repositories so tests can drive persistence directly.
    /// </summary>
    private static async Task<(OpenCodeHarnessSession Instance, InMemoryMessageRepository MessageRepo, HarnessEventPersistenceService PersistenceService)>
        CreateInstanceWithSseLines(
            string fleetSessionId,
            IEnumerable<string> sseLines,
            Action<InMemoryMessageRepository>? configureRepo = null)
    {
        var (scopeFactory, messageRepo, _, _, outboxRepo) = BuildPersistenceDependencies();
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

        var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: fleetSessionId,
            instanceHandle: CreateOwnedHandle(ocHttpClient, "/tmp"),
            workingDirectory: "/tmp",
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: TestUserContext.DefaultUserId);

        // Build a persistence service wired to the same messageRepo so tests can drive persistence directly.
        var sessionRepo2 = new InMemorySessionRepository();
        var delegationRepo2 = new InMemoryDelegationRepository();
        var outboxRepo2 = new InMemoryOutboxRepository();
        var outboxDispatcher2 = new FakeOutboxDispatcher();
        var connectionFactory2 = new FakeDbConnectionFactory();
        var activityWriteService2 = new SessionActivityWriteService(
            connectionFactory2, messageRepo, delegationRepo2, sessionRepo2, new InMemorySmartLinkRepository(), outboxRepo2, outboxDispatcher2);
        var sharedPersistenceService = new HarnessEventPersistenceService(
            messageRepo, sessionRepo2, activityWriteService2, ownerUserId: TestUserContext.DefaultUserId);

        return (instance, messageRepo, sharedPersistenceService);
    }

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    /// <summary>
    /// Consumes all events from the instance's SubscribeAsync stream until the stream ends
    /// (when the fake HTTP handler closes the connection) or the CTS fires.
    /// For each event, drives persistence via the provided <see cref="HarnessEventPersistenceService"/>.
    /// OperationCanceledException from the CT is expected and silently ignored.
    /// </summary>
    private static async Task<List<HarnessEvent>> ConsumeEventsAsync(
        OpenCodeHarnessSession instance,
        CancellationToken ct,
        HarnessEventPersistenceService? persistenceService = null,
        string? fleetSessionId = null)
    {
        var events = new List<HarnessEvent>();
        try
        {
            await foreach (var evt in instance.SubscribeAsync(ct))
            {
                events.Add(evt);
                if (persistenceService is not null && fleetSessionId is not null)
                {
                    persistenceService.BufferTextDelta(fleetSessionId, evt);
                    await persistenceService.TryHandleDurableEventAsync(fleetSessionId, evt)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected — CTS fired after events were processed
        }
        return events;
    }

    [Fact]
    public async Task SubscribeAsync_MessageUpdatedEvent_PersistsMessageIdentity()
    {
        var fleetSessionId = "fleet-persist-1";
        var persistSignal = new TaskCompletionSource();

        var (instance, messageRepo, persistenceService) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [BuildMessageUpdatedSseLine("assistant", "msg-1", "loom")],
            repo =>
            {
                repo.UpsertBehavior = _ =>
                {
                    persistSignal.TrySetResult();
                    return Task.CompletedTask;
                };
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token, persistenceService, fleetSessionId);

        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        messageRepo.UpsertCalls.ShouldContain(m =>
            m.Id == "msg-1"
            && m.SessionId == fleetSessionId
            && m.Role == "assistant"
            && m.AgentName == "loom"
            && m.PartsJson.Contains("Hello"));

        await cts.CancelAsync();
        await consumeTask;

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_MessageUpdatedEvent_WithUppercaseModelIds_PersistsModelIdentity()
    {
        var fleetSessionId = "fleet-persist-model-1";
        var persistSignal = new TaskCompletionSource();

        var (instance, messageRepo, persistenceService) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [BuildAssistantMessageUpdatedSseLineWithUppercaseModelIds("msg-1", "gpt-5.4", "github-copilot")],
            repo =>
            {
                repo.UpsertBehavior = _ =>
                {
                    persistSignal.TrySetResult();
                    return Task.CompletedTask;
                };
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token, persistenceService, fleetSessionId);

        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        messageRepo.UpsertCalls.ShouldContain(m =>
            m.Id == "msg-1"
            && m.SessionId == fleetSessionId
            && m.ModelId == "gpt-5.4");

        await cts.CancelAsync();
        await consumeTask;
        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_MessageUpdatedEvent_WithoutModelId_DoesNotInjectModelMetadata()
    {
        var fleetSessionId = "fleet-persist-model-2";

        var (instance, _, _) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [BuildMessageUpdatedSseLine("assistant", "msg-1", "loom")]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var events = await ConsumeEventsAsync(instance, cts.Token);

        var messageUpdated = events.Last(evt => evt.Type == EventTypes.MessageUpdated);
        var info = messageUpdated.Payload!.Value.GetProperty("info");

        info.TryGetProperty("modelId", out _).ShouldBeFalse();
        info.TryGetProperty("modelID", out _).ShouldBeFalse();
        info.TryGetProperty("providerId", out _).ShouldBeFalse();
        info.TryGetProperty("providerID", out _).ShouldBeFalse();

        await instance.DisposeAsync();
    }

    // User message.created echo persistence is intentionally not tested here.
    // User prompt persistence is owned by SessionOrchestrator; OpenCode user
    // echoes are suppressed to avoid duplicate prompt rows.

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
        var (instance, messageRepo, persistenceService) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [sessionStatusLine]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token, persistenceService, fleetSessionId);

        // Give the event time to be processed, then cancel
        await Task.Delay(300);
        await cts.CancelAsync();
        await consumeTask;

        messageRepo.UpsertCalls.ShouldBeEmpty();

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_PersistenceFailure_DoesNotBlockEventDelivery()
    {
        var fleetSessionId = "fleet-persist-4";
        var eventDeliveredSignal = new TaskCompletionSource();

        var (instance, messageRepo, _) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [BuildMessageCreatedSseLine("assistant", "msg-persist-failure-1")],
            repo =>
            {
                repo.UpsertBehavior = _ => Task.FromException(new InvalidOperationException("DB is on fire"));
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
        events.ShouldNotBeEmpty();
        events[0].Type.ShouldBe("message.created");

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_MessagePartUpdated_TextPart_PersistsIncrementally()
    {
        var fleetSessionId = "fleet-part-persist-1";
        var messageId = "msg-part-1";
        var persistSignal = new TaskCompletionSource();

        var (instance, messageRepo, persistenceService) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [BuildPartUpdatedSseLine(messageId, "oc-session", "Hello from part")],
            repo =>
            {
                repo.GetByIdBehavior = (_, _) => Task.FromResult<PersistedMessage?>(null);
                repo.UpsertBehavior = _ =>
                {
                    persistSignal.TrySetResult();
                    return Task.CompletedTask;
                };
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token, persistenceService, fleetSessionId);

        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await consumeTask;

        messageRepo.UpsertCalls.ShouldContain(m =>
            m.Id == messageId &&
            m.SessionId == fleetSessionId &&
            m.Role == "assistant" &&
            m.PartsJson.Contains("Hello from part"));

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_MessagePartUpdated_WithInfo_BackfillsExistingRoleAndAgent()
    {
        var fleetSessionId = "fleet-part-persist-2";
        var messageId = "msg-part-2";

        PersistedMessage? lastUpserted = new()
        {
            Id = messageId,
            SessionId = fleetSessionId,
            Role = "assistant",
            PartsJson = "[]",
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            AgentName = null,
        };

        var persistSignal = new TaskCompletionSource();

        var (instance, messageRepo, persistenceService) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [BuildPartUpdatedSseLine(messageId, "oc-session", "Hello from part", "user", "loom")],
            repo =>
            {
                repo.GetByIdBehavior = (_, _) => Task.FromResult<PersistedMessage?>(lastUpserted);
                repo.UpsertBehavior = m =>
                {
                    lastUpserted = m;
                    persistSignal.TrySetResult();
                    return Task.CompletedTask;
                };
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token, persistenceService, fleetSessionId);

        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await consumeTask;

        lastUpserted.ShouldNotBeNull();
        lastUpserted.Role.ShouldBe("user");
        lastUpserted.AgentName.ShouldBe("loom");
        lastUpserted.PartsJson.ShouldContain("Hello from part");

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

        var (instance, messageRepo, persistenceService) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [emptyPartsLine],
            repo =>
            {
                repo.GetByIdBehavior = (_, _) => Task.FromResult<PersistedMessage?>(existingMessage);
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token, persistenceService, fleetSessionId);

        // Give fire-and-forget tasks time to settle, then cancel
        await Task.Delay(300);
        await cts.CancelAsync();
        await consumeTask;

        // UpsertAsync must NOT be called — guard prevented overwrite
        messageRepo.UpsertCalls.ShouldBeEmpty();

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

        var (instance, messageRepo, persistenceService) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [
                skeletonLine,
                BuildPartUpdatedSseLine(messageId, "oc-session", "The actual answer"),
                BuildMessageUpdatedSseLine("assistant", messageId)
            ],
            repo =>
            {
                repo.GetByIdBehavior = (_, _) => Task.FromResult(lastUpserted);
                repo.UpsertBehavior = m =>
                {
                    lastUpserted = m;
                    persistedMessages.Add(m);
                    upsertSignal.TrySetResult();
                    return Task.CompletedTask;
                };
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token, persistenceService, fleetSessionId);

        await upsertSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        await cts.CancelAsync();
        await consumeTask;

        // Final state must contain the text part (not overwritten by message.updated)
        lastUpserted.ShouldNotBeNull();
        lastUpserted.PartsJson.ShouldContain("The actual answer");

        // The final persisted state must not be empty
        lastUpserted.PartsJson.ShouldNotBe("[]");

        // At least one upsert must have occurred (from message.part.updated)
        (persistedMessages.Count >= 1).ShouldBeTrue("Expected at least one UpsertAsync call from part.updated");

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_MessageUpdated_PersistsBufferedTextDeltaWhenNoPartSnapshotArrives()
    {
        var fleetSessionId = "fleet-delta-fallback-1";
        var messageId = "msg-delta-fallback-1";

        PersistedMessage? lastUpserted = null;
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
                    agent = "loom",
                    time = new { created = 1700000000L }
                },
                parts = Array.Empty<object>()
            }
        });

        var (instance, messageRepo, persistenceService) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [
                skeletonLine,
                BuildPartDeltaSseLine(messageId, "oc-session", "part-1", "Execution work: no. "),
                BuildPartDeltaSseLine(messageId, "oc-session", "part-1", "Plan completion work: yes."),
                BuildMessageUpdatedSseLine("assistant", messageId, "loom")
            ],
            repo =>
            {
                repo.GetByIdBehavior = (_, _) => Task.FromResult(lastUpserted);
                repo.UpsertBehavior = m =>
                {
                    lastUpserted = m;
                    upsertSignal.TrySetResult();
                    return Task.CompletedTask;
                };
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token, persistenceService, fleetSessionId);

        await upsertSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        await cts.CancelAsync();
        await consumeTask;

        lastUpserted.ShouldNotBeNull();
        lastUpserted.PartsJson.ShouldContain("Execution work: no. Plan completion work: yes.");

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_MessageUpdated_WritesCommittedSnapshotPayloadAfterBufferedDeltaMerge()
    {
        var fleetSessionId = "fleet-delta-outbox-1";
        var messageId = "msg-delta-outbox-1";

        PersistedMessage? lastUpserted = null;
        var upsertSignal = new TaskCompletionSource();
        var (scopeFactory, messageRepo, _, _, outboxRepo) = BuildPersistenceDependencies();

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
                    agent = "loom",
                    time = new { created = 1700000000L }
                },
                parts = Array.Empty<object>()
            }
        });

        var sseBody = new StringBuilder();
        foreach (var line in new[]
                 {
                     skeletonLine,
                     BuildPartDeltaSseLine(messageId, "oc-session", "part-1", "Merged "),
                     BuildPartDeltaSseLine(messageId, "oc-session", "part-1", "response"),
                     BuildMessageUpdatedSseLine("assistant", messageId, "loom")
                 })
        {
            sseBody.AppendLine(line);
            sseBody.AppendLine();
        }

        messageRepo.GetByIdBehavior = (_, _) => Task.FromResult(lastUpserted);
        messageRepo.UpsertBehavior = m =>
        {
            lastUpserted = m;
            upsertSignal.TrySetResult();
            return Task.CompletedTask;
        };

        var handler = new FakeSseHttpMessageHandler(sseBody.ToString());
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var ocHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);

        await using var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: fleetSessionId,
            instanceHandle: CreateOwnedHandle(ocHttpClient, "/tmp"),
            workingDirectory: "/tmp",
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: TestUserContext.DefaultUserId);

        var sessionRepo2 = new InMemorySessionRepository();
        var delegationRepo2 = new InMemoryDelegationRepository();
        var outboxRepo2 = new InMemoryOutboxRepository();
        var outboxDispatcher2 = new FakeOutboxDispatcher();
        var connectionFactory2 = new FakeDbConnectionFactory();
        var activityWriteService2 = new SessionActivityWriteService(
            connectionFactory2, messageRepo, delegationRepo2, sessionRepo2, new InMemorySmartLinkRepository(), outboxRepo, outboxDispatcher2);
        var persistenceService = new HarnessEventPersistenceService(
            messageRepo, sessionRepo2, activityWriteService2, ownerUserId: TestUserContext.DefaultUserId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token, persistenceService, fleetSessionId);

        await upsertSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        await cts.CancelAsync();
        await consumeTask;

        // Harness events no longer write to the outbox — the in-process fan-out service
        // delivers them directly. Verify the persisted message contains the merged
        // text (buffered deltas merged into the MessageUpdated snapshot) and that no harness
        // events at all were written to the outbox.
        lastUpserted.ShouldNotBeNull();
        lastUpserted.PartsJson.ShouldContain("Merged response");
        outboxRepo.All.Count(m => m.Type == "message.updated").ShouldBe(0);
        outboxRepo.All.Count(m => m.Type == "message.part.delta").ShouldBe(0);
    }

    [Fact]
    public async Task SubscribeAsync_MessagePartUpdated_ReasoningPart_DoesNotWriteDurableOutboxPayload()
    {
        var fleetSessionId = "fleet-reasoning-outbox-1";
        var messageId = "msg-reasoning-1";
        var persistSignal = new TaskCompletionSource();
        var (scopeFactory, messageRepo, _, _, outboxRepo) = BuildPersistenceDependencies();

        messageRepo.GetByIdBehavior = (_, _) => Task.FromResult<PersistedMessage?>(null);
        messageRepo.UpsertBehavior = _ =>
        {
            persistSignal.TrySetResult();
            return Task.CompletedTask;
        };

        var sseBody = new StringBuilder();
        sseBody.AppendLine(BuildReasoningPartUpdatedSseLine(messageId, "oc-session", "part-r1", "Hidden thought"));
        sseBody.AppendLine();

        var handler = new FakeSseHttpMessageHandler(sseBody.ToString());
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var ocHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);

        await using var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: fleetSessionId,
            instanceHandle: CreateOwnedHandle(ocHttpClient, "/tmp"),
            workingDirectory: "/tmp",
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: TestUserContext.DefaultUserId);

        var sessionRepo2 = new InMemorySessionRepository();
        var delegationRepo2 = new InMemoryDelegationRepository();
        var outboxRepo2 = new InMemoryOutboxRepository();
        var outboxDispatcher2 = new FakeOutboxDispatcher();
        var connectionFactory2 = new FakeDbConnectionFactory();
        var activityWriteService2 = new SessionActivityWriteService(
            connectionFactory2, messageRepo, delegationRepo2, sessionRepo2, new InMemorySmartLinkRepository(), outboxRepo, outboxDispatcher2);
        var persistenceService = new HarnessEventPersistenceService(
            messageRepo, sessionRepo2, activityWriteService2, ownerUserId: TestUserContext.DefaultUserId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token, persistenceService, fleetSessionId);

        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        await cts.CancelAsync();
        await consumeTask;

        messageRepo.UpsertCalls.ShouldContain(m =>
            m.Id == messageId
            && m.SessionId == fleetSessionId
            && m.Role == "assistant"
            && !m.PartsJson.Contains("Hidden thought", StringComparison.Ordinal));

        outboxRepo.All.ShouldNotContain(message =>
            message.Type == "message.part.updated" && message.Payload.Contains("Hidden thought", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubscribeAsync_MessagePartUpdated_FilePart_PersistsIncrementally()
    {
        var fleetSessionId = "fleet-file-persist-1";
        var messageId = "msg-file-1";
        var persistSignal = new TaskCompletionSource();

        var (instance, messageRepo, persistenceService) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [BuildFilePartUpdatedSseLine(messageId, "oc-session", "file-1", "image/png", "diagram.png", "https://example.test/diagram.png")],
            repo =>
            {
                repo.GetByIdBehavior = (_, _) => Task.FromResult<PersistedMessage?>(null);
                repo.UpsertBehavior = _ =>
                {
                    persistSignal.TrySetResult();
                    return Task.CompletedTask;
                };
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token, persistenceService, fleetSessionId);

        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await consumeTask;

        messageRepo.UpsertCalls.ShouldContain(m =>
            m.Id == messageId
            && m.SessionId == fleetSessionId
            && m.PartsJson.Contains("diagram.png")
            && m.PartsJson.Contains("https://example.test/diagram.png"));

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_MessageUpdated_OverwritesPlaceholderTimestampWithHarnessTime()
    {
        var fleetSessionId = "fleet-authoritative-timestamp-1";
        var messageId = "msg-authoritative-time-1";

        var placeholderTimestamp = DateTimeOffset.UtcNow.ToString("O");
        var placeholderCreatedAt = DateTimeOffset.UtcNow.ToString("O");

        var (instance, messageRepo, persistenceService) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [
                BuildPartUpdatedSseLine(messageId, "oc-session", "placeholder text", "assistant", "loom"),
                BuildMessageUpdatedSseLine("assistant", messageId, "loom")
            ],
            repo =>
            {
                repo.GetByIdBehavior = (id, sessionId) =>
                {
                    if (id != messageId || sessionId != fleetSessionId)
                        return Task.FromResult<PersistedMessage?>(null);

                    var latest = repo.All.LastOrDefault(message => message.Id == id && message.SessionId == sessionId);
                    if (latest is not null)
                        return Task.FromResult<PersistedMessage?>(latest);

                    return Task.FromResult<PersistedMessage?>(new PersistedMessage
                    {
                        Id = messageId,
                        SessionId = fleetSessionId,
                        Role = "assistant",
                        PartsJson = """[{"type":"text","text":"placeholder text"}]""",
                        Timestamp = placeholderTimestamp,
                        CreatedAt = placeholderCreatedAt,
                        AgentName = "loom",
                    });
                };
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token, persistenceService, fleetSessionId);

        await Task.Delay(300);
        await cts.CancelAsync();
        await consumeTask;

        var persisted = messageRepo.All.Last(message => message.Id == messageId && message.SessionId == fleetSessionId);
        persisted.Timestamp.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000).ToString("O"));
        persisted.CreatedAt.ShouldBe(placeholderCreatedAt);

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_MessagePartUpdated_StepFinishPart_PersistsIncrementally()
    {
        var fleetSessionId = "fleet-step-persist-1";
        var messageId = "msg-step-1";
        var persistSignal = new TaskCompletionSource();

        var (instance, messageRepo, persistenceService) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [BuildStepFinishPartUpdatedSseLine(messageId, "oc-session", "step-1", 3, "completed")],
            repo =>
            {
                repo.GetByIdBehavior = (_, _) => Task.FromResult<PersistedMessage?>(null);
                repo.UpsertBehavior = _ =>
                {
                    persistSignal.TrySetResult();
                    return Task.CompletedTask;
                };
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token, persistenceService, fleetSessionId);

        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await consumeTask;

        messageRepo.UpsertCalls.ShouldContain(m =>
            m.Id == messageId
            && m.SessionId == fleetSessionId
            && m.PartsJson.Contains("step-finish")
            && m.PartsJson.Contains("completed"));

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_TaskToolEvent_CreatesDelegationAndBroadcasts()
    {
        var messageRepo = new InMemoryMessageRepository();
        var delegationRepo = new InMemoryDelegationRepository();
        var eventBroadcaster = new FakeEventBroadcaster();

        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(messageRepo);
        services.AddSingleton<IDelegationRepository>(delegationRepo);
        services.AddSingleton<IEventBroadcaster>(eventBroadcaster);
        services.AddSingleton<IUserContext>(new TestUserContext("user-1"));
        services.AddSingleton(new DelegationService(delegationRepo, eventBroadcaster, new TestUserContext("user-1")));
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

        var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: "fleet-delegation-1",
            instanceHandle: CreateOwnedHandle(ocHttpClient, "/tmp"),
            workingDirectory: "/tmp",
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: "user-1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

        await Task.Delay(300);
        await cts.CancelAsync();
        await consumeTask;

        delegationRepo.InsertedDelegations.ShouldContain(d =>
            d.ParentSessionId == "fleet-delegation-1" &&
            d.ParentToolCallId == "tool-1" &&
            d.Title == "reviewer" &&
            d.Status == "pending");

        eventBroadcaster.Broadcasts.ShouldContain(b =>
            b.Topic == "session:fleet-delegation-1" &&
            b.Type == "delegation.created");

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_TaskToolRunningWithChildSession_LinksDelegationAndBroadcastsUpdate()
    {
        var delegationRepo = new InMemoryDelegationRepository();
        var eventBroadcaster = new FakeEventBroadcaster();
        var sessionRepo = new InMemorySessionRepository();

        sessionRepo.Seed(new Session
        {
            Id = "fleet-delegation-1",
            WorkspaceId = "ws-1",
            InstanceId = "inst-parent",
            HarnessType = "opencode",
            Title = "Parent",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = "user-1"
        });

        Session? persistedChildSession = null;
        sessionRepo.GetByHarnessIdBehavior = _ => Task.FromResult(persistedChildSession);

        // First call returns null, second call returns the delegation
        var callCount = 0;
        var delegation = new Delegation
        {
            Id = "del-1",
            ParentSessionId = "fleet-delegation-1",
            ParentToolCallId = "tool-1",
            Title = "reviewer",
            Status = "pending",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        };
        delegationRepo.GetByParentToolCallIdBehavior = (_, _) =>
        {
            callCount++;
            return callCount == 1
                ? Task.FromResult<Delegation?>(null)
                : Task.FromResult<Delegation?>(delegation);
        };

        var userContext = new TestUserContext("user-1");
        var options = new FleetOptions();

        var builder = new SessionOrchestratorBuilder()
            .WithUserContext(userContext)
            .WithOptions(options);

        // Override the builder's repos with our configured ones
        var runtime = builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true });
        runtime.PrepareRuntimeBehavior = (_, _) =>
            Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new StubLaunchArtifacts()));
        var childHarnessInstance = new FakeHarnessSession("inst-child") { HarnessType = "opencode", Status = HarnessSessionStatus.Running };
        runtime.ResumeBehavior = (_, _) => Task.FromResult<IHarnessSession>(childHarnessInstance);

        builder.WorkspaceRootRepository.Seed(new WorkspaceRoot
        {
            Id = "root-1",
            Path = Path.GetTempPath(),
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(new InMemoryMessageRepository());
        services.AddSingleton<IDelegationRepository>(delegationRepo);
        services.AddSingleton<IEventBroadcaster>(eventBroadcaster);
        services.AddSingleton<ISessionRepository>(sessionRepo);
        services.AddSingleton<IInstanceRepository>(builder.InstanceRepository);
        services.AddSingleton<IProjectRepository>(builder.ProjectRepository);
        services.AddSingleton<ISessionCallbackRepository>(builder.SessionCallbackRepository);
        services.AddSingleton<IWorkspaceRepository>(builder.WorkspaceRepository);
        services.AddSingleton<ISessionSourceUsageRepository>(builder.SessionSourceUsageRepository);
        services.AddSingleton<IHarnessRegistry>(builder.HarnessRegistry);
        services.AddSingleton<IAnalyticsCollector>(builder.AnalyticsCollector);
        services.AddSingleton(new InstanceTracker());
        services.AddSingleton<IUserContext>(userContext);
        services.AddSingleton(new InstanceService(builder.InstanceRepository, sessionRepo, userContext));
        services.AddSingleton(new DelegationService(delegationRepo, eventBroadcaster, userContext));
        var workspaceRootService = new WorkspaceRootService(builder.WorkspaceRootRepository, userContext);
        services.AddSingleton(new SessionSourceResolutionService([
            new LocalDirectorySessionSourceProvider(workspaceRootService)
        ]));
        services.AddSingleton(new SessionOrchestrator(
            new WorkspaceService(builder.WorkspaceRepository, userContext, options, NullLogger<WorkspaceService>.Instance),
            new InstanceService(builder.InstanceRepository, sessionRepo, userContext),
            new SessionSourceResolutionService([
                new LocalDirectorySessionSourceProvider(workspaceRootService)
            ]),
            builder.HarnessRegistry,
            new InstanceTracker(),
            sessionRepo,
            builder.SessionSourceUsageRepository,
            builder.SessionCallbackRepository,
            delegationRepo,
            builder.ProjectRepository,
            eventBroadcaster,
            builder.AnalyticsCollector,
            new InMemoryMessageRepository(),
            new DelegationService(delegationRepo, eventBroadcaster, userContext),
            builder.CredentialStore,
            userContext,
            options,
            new InMemorySmartLinkRepository(),
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

        var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: "fleet-delegation-1",
            instanceHandle: CreateOwnedHandle(ocHttpClient, "/tmp"),
            workingDirectory: "/tmp",
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: "user-1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

        await Task.Delay(300);
        await cts.CancelAsync();
        await consumeTask;

        delegationRepo.UpdateChildSessionIdCalls.ShouldContain(c => c.Id == "del-1");

        eventBroadcaster.Broadcasts.ShouldContain(b =>
            b.Topic == "session:fleet-delegation-1" &&
            b.Type == "delegation.updated");

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_SubtaskPartWithChildSession_LinksDelegationAndBroadcastsUpdate()
    {
        var delegationRepo = new InMemoryDelegationRepository();
        var eventBroadcaster = new FakeEventBroadcaster();
        var sessionRepo = new InMemorySessionRepository();

        sessionRepo.Seed(new Session
        {
            Id = "fleet-delegation-1",
            WorkspaceId = "ws-1",
            InstanceId = "inst-parent",
            HarnessType = "opencode",
            Title = "Parent",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = "user-1"
        });

        Session? persistedChildSession = null;
        sessionRepo.GetByHarnessIdBehavior = _ => Task.FromResult(persistedChildSession);

        delegationRepo.Seed(new Delegation
        {
            Id = "del-1",
            ParentSessionId = "fleet-delegation-1",
            ParentToolCallId = "tool-1",
            Title = "thread",
            Status = "pending",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        });

        var userContext = new TestUserContext("user-1");
        var options = new FleetOptions();

        var builder = new SessionOrchestratorBuilder()
            .WithUserContext(userContext)
            .WithOptions(options);

        var runtime = builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true });
        runtime.PrepareRuntimeBehavior = (_, _) =>
            Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new StubLaunchArtifacts()));
        var childHarnessInstance = new FakeHarnessSession("inst-child") { HarnessType = "opencode", Status = HarnessSessionStatus.Running };
        runtime.ResumeBehavior = (_, _) => Task.FromResult<IHarnessSession>(childHarnessInstance);

        builder.WorkspaceRootRepository.Seed(new WorkspaceRoot
        {
            Id = "root-1",
            Path = Path.GetTempPath(),
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(new InMemoryMessageRepository());
        services.AddSingleton<IDelegationRepository>(delegationRepo);
        services.AddSingleton<IEventBroadcaster>(eventBroadcaster);
        services.AddSingleton<ISessionRepository>(sessionRepo);
        services.AddSingleton<IInstanceRepository>(builder.InstanceRepository);
        services.AddSingleton<IProjectRepository>(builder.ProjectRepository);
        services.AddSingleton<ISessionCallbackRepository>(builder.SessionCallbackRepository);
        services.AddSingleton<IWorkspaceRepository>(builder.WorkspaceRepository);
        services.AddSingleton<ISessionSourceUsageRepository>(builder.SessionSourceUsageRepository);
        services.AddSingleton<IHarnessRegistry>(builder.HarnessRegistry);
        services.AddSingleton<IAnalyticsCollector>(builder.AnalyticsCollector);
        services.AddSingleton(new InstanceTracker());
        services.AddSingleton<IUserContext>(userContext);
        services.AddSingleton(new InstanceService(builder.InstanceRepository, sessionRepo, userContext));
        services.AddSingleton(new DelegationService(delegationRepo, eventBroadcaster, userContext));
        var workspaceRootService = new WorkspaceRootService(builder.WorkspaceRootRepository, userContext);
        services.AddSingleton(new SessionSourceResolutionService([
            new LocalDirectorySessionSourceProvider(workspaceRootService)
        ]));
        services.AddSingleton(new SessionOrchestrator(
            new WorkspaceService(builder.WorkspaceRepository, userContext, options, NullLogger<WorkspaceService>.Instance),
            new InstanceService(builder.InstanceRepository, sessionRepo, userContext),
            new SessionSourceResolutionService([
                new LocalDirectorySessionSourceProvider(workspaceRootService)
            ]),
            builder.HarnessRegistry,
            new InstanceTracker(),
            sessionRepo,
            builder.SessionSourceUsageRepository,
            builder.SessionCallbackRepository,
            delegationRepo,
            builder.ProjectRepository,
            eventBroadcaster,
            builder.AnalyticsCollector,
            new InMemoryMessageRepository(),
            new DelegationService(delegationRepo, eventBroadcaster, userContext),
            builder.CredentialStore,
            userContext,
            options,
            new InMemorySmartLinkRepository(),
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

        var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: "fleet-delegation-1",
            instanceHandle: CreateOwnedHandle(ocHttpClient, "/tmp"),
            workingDirectory: "/tmp",
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: "user-1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

        await Task.Delay(300);
        await cts.CancelAsync();
        await consumeTask;

        delegationRepo.UpdateChildSessionIdCalls.ShouldContain(c => c.Id == "del-1");

        eventBroadcaster.Broadcasts.ShouldContain(b =>
            b.Topic == "session:fleet-delegation-1" &&
            b.Type == "delegation.updated");

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_RoutesEventsFromDelegatedChildOpenCodeSession()
    {
        var fleetSessionId = "fleet-parent";
        var connectionFactory = new FakeDbConnectionFactory();

        var messageRepo = new InMemoryMessageRepository();
        var delegationRepo = new InMemoryDelegationRepository();
        var eventBroadcaster = new FakeEventBroadcaster();
        var sessionRepo = new InMemorySessionRepository();
        var outboxRepo = new InMemoryOutboxRepository();
        var outboxDispatcher = new FakeOutboxDispatcher();

        sessionRepo.Seed(new Session
        {
            Id = "fleet-child",
            ParentSessionId = "fleet-parent",
            InstanceId = "inst-child",
            OpencodeSessionId = "oc-child"
        });

        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(messageRepo);
        services.AddSingleton<IDelegationRepository>(delegationRepo);
        services.AddSingleton<IEventBroadcaster>(eventBroadcaster);
        services.AddSingleton<ISessionRepository>(sessionRepo);
        services.AddSingleton<IDbConnectionFactory>(connectionFactory);
        services.AddSingleton<IOutboxRepository>(outboxRepo);
        services.AddSingleton<IOutboxDispatcher>(outboxDispatcher);
        services.AddSingleton<IUserContext>(new TestUserContext("user-1"));
        services.AddSingleton(new SessionActivityWriteService(
            connectionFactory,
            messageRepo,
            delegationRepo,
            sessionRepo,
            new InMemorySmartLinkRepository(),
            outboxRepo,
            outboxDispatcher));
        services.AddSingleton(new DelegationService(delegationRepo, eventBroadcaster, new TestUserContext("user-1")));
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        var childEventLine = BuildPartUpdatedSseLine("msg-child", "oc-child", "child text");
        var sseBody = new StringBuilder();
        sseBody.AppendLine(childEventLine);
        sseBody.AppendLine();

        var handler = new FakeSseHttpMessageHandler(sseBody.ToString());
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var ocHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);

        var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: fleetSessionId,
            instanceHandle: CreateOwnedHandle(ocHttpClient, "/tmp"),
            workingDirectory: "/tmp",
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: "user-1",
            openCodeSessionId: "oc-parent");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);
        await Task.Delay(300);
        await cts.CancelAsync();
        var events = await consumeTask;

        // SubscribeAsync is now a pure event producer — it yields child-routed events
        // with FleetSessionId set so the relay can route them to the correct topic.
        // Persistence is handled by HarnessEventPersistenceService in the relay pump.
        events.ShouldContain(e =>
            e.Type == "message.part.updated"
            && e.FleetSessionId == "fleet-child");

        await instance.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // Tests: new event type handlers (message.removed, message.part.removed,
    //        session.updated, session.error, session.compacted, session.deleted)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MessageRemoved_DeletesMessageFromRepository()
    {
        var fleetSessionId = "fleet-msg-removed-1";
        var messageId = "msg-to-delete";

        var (service, messageRepo, _) = BuildPersistenceService(configureMessageRepo: repo =>
        {
            repo.Seed(new PersistedMessage
            {
                Id = messageId,
                SessionId = fleetSessionId,
                Role = "assistant",
                PartsJson = "[]",
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
        });

        var evt = BuildEvent("message.removed", new { id = messageId });
        var handled = await service.TryHandleDurableEventAsync(fleetSessionId, evt);

        handled.ShouldBeTrue();
        messageRepo.All.ShouldNotContain(m => m.Id == messageId && m.SessionId == fleetSessionId);
    }

    [Fact]
    public async Task MessageRemoved_DoesNotEmitOutboxEvent()
    {
        // Harness events no longer travel through the outbox — the in-process fan-out service
        // delivers message.removed directly.
        var fleetSessionId = "fleet-msg-removed-2";
        var messageId = "msg-to-delete-2";

        var (service, _, outboxRepo) = BuildPersistenceService();

        var evt = BuildEvent("message.removed", new { id = messageId });
        await service.TryHandleDurableEventAsync(fleetSessionId, evt);

        outboxRepo.All.ShouldNotContain(m => m.Type == "message.removed");
    }

    [Fact]
    public async Task MessagePartRemoved_RemovesPartFromMessage()
    {
        var fleetSessionId = "fleet-part-removed-1";
        var messageId = "msg-with-parts";
        var partId = "part-to-remove";

        var (service, messageRepo, _) = BuildPersistenceService(configureMessageRepo: repo =>
        {
            repo.Seed(new PersistedMessage
            {
                Id = messageId,
                SessionId = fleetSessionId,
                Role = "assistant",
                PartsJson = "[{\"id\":\"" + partId + "\",\"type\":\"text\",\"text\":\"hello\"},{\"id\":\"part-keep\",\"type\":\"text\",\"text\":\"keep\"}]",
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
        });

        var evt = BuildEvent("message.part.removed", new { messageID = messageId, id = partId });
        var handled = await service.TryHandleDurableEventAsync(fleetSessionId, evt);

        handled.ShouldBeTrue();
        var updated = messageRepo.All.FirstOrDefault(m => m.Id == messageId && m.SessionId == fleetSessionId);
        updated.ShouldNotBeNull();
        updated.PartsJson.ShouldNotContain(partId);
        updated.PartsJson.ShouldContain("part-keep");
    }

    [Fact]
    public async Task MessagePartRemoved_DoesNotEmitOutboxEvent()
    {
        var fleetSessionId = "fleet-part-removed-2";

        var (service, _, outboxRepo) = BuildPersistenceService();

        var evt = BuildEvent("message.part.removed", new { messageID = "msg-1", id = "part-1" });
        await service.TryHandleDurableEventAsync(fleetSessionId, evt);

        outboxRepo.All.ShouldNotContain(m => m.Type == "message.part.removed");
    }

    [Fact]
    public async Task SessionUpdated_UpdatesTitleInRepository()
    {
        var fleetSessionId = "fleet-session-updated-1";

        var (service, _, outboxRepo) = BuildPersistenceService(configureSessionRepo: repo =>
        {
            repo.Seed(new Session
            {
                Id = fleetSessionId,
                Title = "Old Title",
                Status = "active",
                CreatedAt = DateTime.UtcNow.ToString("O"),
                UserId = "user-1"
            });
        });

        var evt = BuildEvent("session.updated", new { title = "New Title" });
        var handled = await service.TryHandleDurableEventAsync(fleetSessionId, evt);

        handled.ShouldBeTrue();
        // Title is persisted via the session repository; no outbox write — clients receive the
        // session.updated event via the in-process fan-out service.
        outboxRepo.All.ShouldNotContain(m => m.Type == "session.updated");
    }

    [Theory]
    [InlineData("session.error", "{ \"error\": \"boom\" }")]
    [InlineData("session.compacted", "{ }")]
    [InlineData("session.deleted", "{ }")]
    public async Task SessionLifecycleEvents_AreDurableAndBypassOutbox(string eventType, string payloadJson)
    {
        // Session lifecycle signals (error/compacted/deleted) are classified durable so they
        // are captured by the event store for replay, but the handler has no DB side-effect and
        // no outbox write. Clients receive the event via the unified fan-out subscriber.
        var fleetSessionId = $"fleet-{eventType}-1";
        var (service, _, outboxRepo) = BuildPersistenceService();
        var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson);

        var evt = new HarnessEvent
        {
            Type = eventType,
            SessionId = fleetSessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = payload,
        };

        var handled = await service.TryHandleDurableEventAsync(fleetSessionId, evt);

        handled.ShouldBeTrue();
        outboxRepo.All.ShouldNotContain(m => m.Type == eventType);
    }

    [Fact]
    public async Task DurableEventsPersistedEvenWithNoFrontendSubscriber()
    {
        // Verifies that persistence happens in the relay pump regardless of whether
        // any frontend WebSocket subscriber is connected.
        var fleetSessionId = "fleet-no-subscriber-1";
        var messageId = "msg-no-sub-1";

        var (service, messageRepo, _) = BuildPersistenceService();

        // Simulate a message.updated event arriving with no frontend subscriber
        var evt = new HarnessEvent
        {
            Type = "message.updated",
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id = messageId,
                    sessionId = "oc-session",
                    role = "assistant",
                    time = new { created = 1700000000L }
                },
                parts = new[] { new { type = "text", id = "p1", sessionId = "oc-session", messageId, text = "Persisted without subscriber" } }
            })
        };

        var handled = await service.TryHandleDurableEventAsync(fleetSessionId, evt);

        handled.ShouldBeTrue();
        messageRepo.UpsertCalls.ShouldContain(m =>
            m.Id == messageId &&
            m.SessionId == fleetSessionId &&
            m.PartsJson.Contains("Persisted without subscriber"));
    }

    [Fact]
    public async Task DeltaBuffer_FlushedOnDisconnect_PreservesPartialContent()
    {
        var fleetSessionId = "fleet-delta-flush-1";
        var messageId = "msg-delta-flush-1";

        var (service, messageRepo, _) = BuildPersistenceService(configureMessageRepo: repo =>
        {
            repo.Seed(new PersistedMessage
            {
                Id = messageId,
                SessionId = fleetSessionId,
                Role = "assistant",
                PartsJson = "[]",
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
        });

        // Buffer some deltas (simulating streaming that was interrupted before message.updated)
        var delta1 = new HarnessEvent
        {
            Type = "message.part.delta",
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                messageID = messageId,
                partID = "part-1",
                field = "text",
                delta = "Partial "
            })
        };
        var delta2 = new HarnessEvent
        {
            Type = "message.part.delta",
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                messageID = messageId,
                partID = "part-1",
                field = "text",
                delta = "content"
            })
        };

        service.BufferTextDelta(fleetSessionId, delta1);
        service.BufferTextDelta(fleetSessionId, delta2);

        // Flush on disconnect
        await service.FlushBufferedDeltasAsync(fleetSessionId);

        messageRepo.UpsertCalls.ShouldContain(m =>
            m.Id == messageId &&
            m.SessionId == fleetSessionId &&
            m.PartsJson.Contains("Partial content"));
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

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];
        public List<string> RequestBodies { get; } = [];
        public string AgentsResponseJson { get; set; } = "[]";

        public Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => SendAsync(request, cancellationToken);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            if (request.Content is not null)
                RequestBodies.Add(request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/session")
            {
                return Task.FromResult(CreateJsonResponse("""
                    {"id":"oc-session-1","slug":"sess","directory":"/tmp","time":{"created":1,"updated":1}}
                    """));
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.Contains("/prompt_async", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.Contains("/command", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/agent")
            {
                return Task.FromResult(CreateJsonResponse(AgentsResponseJson));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

    private static HttpResponseMessage CreateJsonResponse(string json)
    {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
                }
            };
        }
    }

    private sealed class CompositeHttpMessageHandler(string sseBody, RecordingHttpMessageHandler delegateHandler) : HttpMessageHandler
    {
        private int _eventResponsesServed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/event")
            {
                if (Interlocked.Increment(ref _eventResponsesServed) > 1)
                    throw new OperationCanceledException(cancellationToken);

                var content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream");
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                return Task.FromResult(response);
            }

            return delegateHandler.SendCoreAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task SendPromptAsync_WithQualifiedModelId_SendsSplitModelReference()
    {
        var handler = new RecordingHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var openCodeHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);
        var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: "fleet-session",
            instanceHandle: CreateStartedOwnedHandle(openCodeHttpClient, "/tmp"),
            workingDirectory: "/tmp",
            scopeFactory: BuildPersistenceDependencies().ScopeFactory,
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: TestUserContext.DefaultUserId,
            openCodeSessionId: "oc-session");

        await instance.SendPromptAsync("Hello", new PromptOptions { ModelId = "github-copilot/gpt-5.4" }, CancellationToken.None);

        var body = handler.RequestBodies.Last();
        body.ShouldContain("\"providerID\":\"github-copilot\"");
        body.ShouldContain("\"modelID\":\"gpt-5.4\"");
        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SendCommandAsync_WithProviderAndBareModel_SendsCombinedModelString()
    {
        var handler = new RecordingHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var openCodeHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);
        var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: "fleet-session",
            instanceHandle: CreateStartedOwnedHandle(openCodeHttpClient, "/tmp"),
            workingDirectory: "/tmp",
            scopeFactory: BuildPersistenceDependencies().ScopeFactory,
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: TestUserContext.DefaultUserId,
            openCodeSessionId: "oc-session");

        await instance.SendCommandAsync(new CommandOptions { Command = "test", ProviderId = "github-copilot", ModelId = "gpt-5.4" }, CancellationToken.None);

        var body = handler.RequestBodies.Last();
        body.ShouldContain("\"model\":\"github-copilot/gpt-5.4\"");
        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_MessageUpdatedEvent_WithoutModelId_UsesExactAgentFallbackOnly()
    {
        var handler = new RecordingHttpMessageHandler
        {
            AgentsResponseJson = "[{\"name\":\"loom\",\"model\":{\"providerId\":\"github-copilot\",\"modelId\":\"gpt-5.4\"}}]"
        };

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var openCodeHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);
        var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: "fleet-session",
            instanceHandle: CreateOwnedHandle(openCodeHttpClient, "/tmp"),
            workingDirectory: "/tmp",
            scopeFactory: BuildPersistenceDependencies().ScopeFactory,
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: TestUserContext.DefaultUserId,
            openCodeSessionId: "oc-session");

        var messageUpdated = BuildEvent(EventTypes.MessageUpdated, new
        {
            info = new
            {
                id = "msg-1",
                sessionId = "oc-session",
                role = "assistant",
                agent = "loom",
                time = new { created = 1700000000L }
            }
        });

        var method = typeof(OpenCodeHarnessSession).GetMethod(
            "EnrichWithModelInfoWhenMissingAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.ShouldNotBeNull();
        var enrichTask = (Task<HarnessEvent>)method.Invoke(instance, [messageUpdated, CancellationToken.None])!;
        var enriched = await enrichTask;
        var info = enriched.Payload!.Value.GetProperty("info");

        info.GetProperty("modelId").GetString().ShouldBe("gpt-5.4");
        info.GetProperty("providerId").GetString().ShouldBe("github-copilot");

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task MessagePartUpdated_BufferedDeltasLongerThanSnapshot_PreservesFullAccumulatedText()
    {
        var fleetSessionId = "fleet-delta-race-1";
        var messageId = "msg-delta-race-1";
        var partId = "part-race-1";

        var (service, messageRepo, _) = BuildPersistenceService(configureMessageRepo: repo =>
        {
            repo.Seed(new PersistedMessage
            {
                Id = messageId,
                SessionId = fleetSessionId,
                Role = "assistant",
                PartsJson = "[]",
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
        });

        // Buffer deltas that arrived before the harness built its snapshot
        var delta1 = new HarnessEvent
        {
            Type = "message.part.delta",
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                messageID = messageId,
                partID = partId,
                field = "text",
                delta = "Hello "
            })
        };
        var delta2 = new HarnessEvent
        {
            Type = "message.part.delta",
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                messageID = messageId,
                partID = partId,
                field = "text",
                delta = "world"
            })
        };

        service.BufferTextDelta(fleetSessionId, delta1);
        service.BufferTextDelta(fleetSessionId, delta2);

        // The harness snapshot only contains the first delta (race: snapshot was built before delta2 arrived)
        var partUpdatedEvt = new HarnessEvent
        {
            Type = "message.part.updated",
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                part = new
                {
                    id = partId,
                    sessionID = "oc-session",
                    messageID = messageId,
                    type = "text",
                    text = "Hello " // snapshot is missing "world"
                }
            })
        };

        var handled = await service.TryHandleDurableEventAsync(fleetSessionId, partUpdatedEvt);

        handled.ShouldBeTrue();
        messageRepo.UpsertCalls.ShouldContain(m =>
            m.Id == messageId &&
            m.SessionId == fleetSessionId &&
            m.PartsJson.Contains("Hello world"));
    }

    [Fact]
    public async Task MessagePartUpdated_SnapshotLongerThanBufferedDeltas_UsesSnapshotText()
    {
        var fleetSessionId = "fleet-delta-race-2";
        var messageId = "msg-delta-race-2";
        var partId = "part-race-2";

        var (service, messageRepo, _) = BuildPersistenceService(configureMessageRepo: repo =>
        {
            repo.Seed(new PersistedMessage
            {
                Id = messageId,
                SessionId = fleetSessionId,
                Role = "assistant",
                PartsJson = "[]",
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
        });

        // Buffer a short delta
        var delta = new HarnessEvent
        {
            Type = "message.part.delta",
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                messageID = messageId,
                partID = partId,
                field = "text",
                delta = "Hi"
            })
        };

        service.BufferTextDelta(fleetSessionId, delta);

        // The harness snapshot contains more text (normal case — snapshot wins)
        var partUpdatedEvt = new HarnessEvent
        {
            Type = "message.part.updated",
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                part = new
                {
                    id = partId,
                    sessionID = "oc-session",
                    messageID = messageId,
                    type = "text",
                    text = "Hi there, full response"
                }
            })
        };

        var handled = await service.TryHandleDurableEventAsync(fleetSessionId, partUpdatedEvt);

        handled.ShouldBeTrue();
        messageRepo.UpsertCalls.ShouldContain(m =>
            m.Id == messageId &&
            m.SessionId == fleetSessionId &&
            m.PartsJson.Contains("Hi there, full response"));
    }

    private sealed record StubLaunchArtifacts : RuntimeLaunchArtifacts;
}
