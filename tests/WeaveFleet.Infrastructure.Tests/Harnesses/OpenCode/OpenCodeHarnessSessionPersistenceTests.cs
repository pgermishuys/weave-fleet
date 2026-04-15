using System.Net;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

/// <summary>
/// Tests that <see cref="OpenCodeHarnessSession"/> persists messages to the database
/// via <see cref="IMessageRepository"/> when processing SSE events in SubscribeAsync.
/// These tests were relocated from HarnessEventRelayTests after the persistence logic
/// was moved from the relay into the instance (instance-owned persistence pattern).
/// </summary>
public sealed class OpenCodeHarnessSessionPersistenceTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

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
    /// Builds a valid OpenCode message.created event SSE line with the given role and messageId.
    /// </summary>
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

    /// <summary>
    /// Creates an <see cref="OpenCodeHarnessSession"/> backed by a fake SSE HTTP handler
    /// that streams the provided SSE lines.
    /// </summary>
    private static async Task<(OpenCodeHarnessSession Instance, InMemoryMessageRepository MessageRepo)>
        CreateInstanceWithSseLines(
            string fleetSessionId,
            IEnumerable<string> sseLines,
            Action<InMemoryMessageRepository>? configureRepo = null)
    {
        var (scopeFactory, messageRepo, _, _, _) = BuildPersistenceDependencies();
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

        var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: fleetSessionId,
            httpClient: ocHttpClient,
            processManager: processManager,
            portAllocator: new PortAllocator(10000, 10099),
            allocatedPort: allocatedPort,
            workingDirectory: "/tmp",
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: TestUserContext.DefaultUserId);

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
        OpenCodeHarnessSession instance,
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
    public async Task SubscribeAsync_MessageUpdatedEvent_PersistsMessageIdentity()
    {
        var fleetSessionId = "fleet-persist-1";
        var persistSignal = new TaskCompletionSource();

        var (instance, messageRepo) = await CreateInstanceWithSseLines(
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
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        messageRepo.UpsertCalls.ShouldContain(m =>
            m.Id == "msg-1"
            && m.SessionId == fleetSessionId
            && m.Role == "assistant"
            && m.AgentName == "loom"
            && m.PartsJson.Contains("Hello"));

        await cts.CancelAsync();
        var capturedEvents = await consumeTask;
        capturedEvents.ShouldBeEmpty();

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
                repo.UpsertBehavior = _ =>
                {
                    persistSignal.TrySetResult();
                    return Task.CompletedTask;
                };
            });

        // Start consuming in background — the stream reconnects, so we cancel after persist fires
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

        // Wait for persist to fire, then cancel the stream
        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await consumeTask;

        messageRepo.UpsertCalls.ShouldContain(m =>
            m.SessionId == fleetSessionId && m.Role == "user");

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

        messageRepo.UpsertCalls.ShouldBeEmpty();

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_PersistenceFailure_DoesNotBlockEventDelivery()
    {
        var fleetSessionId = "fleet-persist-4";
        var eventDeliveredSignal = new TaskCompletionSource();

        var (instance, messageRepo) = await CreateInstanceWithSseLines(
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

        var (instance, messageRepo) = await CreateInstanceWithSseLines(
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
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

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

        var (instance, messageRepo) = await CreateInstanceWithSseLines(
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
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

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

        var (instance, messageRepo) = await CreateInstanceWithSseLines(
            fleetSessionId,
            [emptyPartsLine],
            repo =>
            {
                repo.GetByIdBehavior = (_, _) => Task.FromResult<PersistedMessage?>(existingMessage);
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

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

        var (instance, messageRepo) = await CreateInstanceWithSseLines(
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
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

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

        var (instance, messageRepo) = await CreateInstanceWithSseLines(
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
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

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
        var processManager = new OpenCodeProcessManager(NullLogger<OpenCodeProcessManager>.Instance);

        await using var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: fleetSessionId,
            httpClient: ocHttpClient,
            processManager: processManager,
            portAllocator: new PortAllocator(10000, 10099),
            allocatedPort: 0,
            workingDirectory: "/tmp",
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: TestUserContext.DefaultUserId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

        await upsertSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        await cts.CancelAsync();
        await consumeTask;

        var outboxMessages = outboxRepo.All;
        var committedMessageUpdate = outboxMessages.Last(message => message.Type == "message.updated");
        var payload = JsonSerializer.Deserialize<JsonElement>(committedMessageUpdate.Payload);
        payload.GetProperty("parts")[0].GetProperty("text").GetString().ShouldBe("Merged response");
        outboxMessages.Count(message => message.Type == "message.part.delta").ShouldBe(0);
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
        var processManager = new OpenCodeProcessManager(NullLogger<OpenCodeProcessManager>.Instance);

        await using var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: fleetSessionId,
            httpClient: ocHttpClient,
            processManager: processManager,
            portAllocator: new PortAllocator(10000, 10099),
            allocatedPort: 0,
            workingDirectory: "/tmp",
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: TestUserContext.DefaultUserId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

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

        var (instance, messageRepo) = await CreateInstanceWithSseLines(
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
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

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
    public async Task SubscribeAsync_MessagePartUpdated_StepFinishPart_PersistsIncrementally()
    {
        var fleetSessionId = "fleet-step-persist-1";
        var messageId = "msg-step-1";
        var persistSignal = new TaskCompletionSource();

        var (instance, messageRepo) = await CreateInstanceWithSseLines(
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
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

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
        var processManager = new OpenCodeProcessManager(NullLogger<OpenCodeProcessManager>.Instance);

        var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: "fleet-delegation-1",
            httpClient: ocHttpClient,
            processManager: processManager,
            portAllocator: new PortAllocator(10000, 10099),
            allocatedPort: 0,
            workingDirectory: "/tmp",
            shutdownTimeout: TimeSpan.FromSeconds(1),
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

        var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: "fleet-delegation-1",
            httpClient: ocHttpClient,
            processManager: processManager,
            portAllocator: new PortAllocator(10000, 10099),
            allocatedPort: 0,
            workingDirectory: "/tmp",
            shutdownTimeout: TimeSpan.FromSeconds(1),
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

        var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: "fleet-delegation-1",
            httpClient: ocHttpClient,
            processManager: processManager,
            portAllocator: new PortAllocator(10000, 10099),
            allocatedPort: 0,
            workingDirectory: "/tmp",
            shutdownTimeout: TimeSpan.FromSeconds(1),
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
        var processManager = new OpenCodeProcessManager(NullLogger<OpenCodeProcessManager>.Instance);

        var instance = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: fleetSessionId,
            httpClient: ocHttpClient,
            processManager: processManager,
            portAllocator: new PortAllocator(10000, 10099),
            allocatedPort: 0,
            workingDirectory: "/tmp",
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: "user-1",
            openCodeSessionId: "oc-parent");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);
        await Task.Delay(300);
        await cts.CancelAsync();
        var events = await consumeTask;

        events.ShouldBeEmpty();
        messageRepo.UpsertCalls.ShouldContain(m =>
            m.Id == "msg-child"
            && m.SessionId == "fleet-child"
            && m.PartsJson.Contains("child text"));

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

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri?.PathAndQuery ?? string.Empty);

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

    private sealed record StubLaunchArtifacts : RuntimeLaunchArtifacts;
}
