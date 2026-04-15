using System.Net;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

/// <summary>
/// Tests that <see cref="OpenCodeHarnessSession"/> persists messages to the database
/// via <see cref="IMessageRepository"/> when processing SSE events in SubscribeAsync.
/// These tests were relocated from HarnessEventRelayTests after the persistence logic
/// was moved from the relay into the instance (instance-owned persistence pattern).
/// </summary>
public sealed class OpenCodeHarnessSessionPersistenceTests
{
    private sealed class StubConnectionFactory(IDbConnection connection) : IDbConnectionFactory
    {
        public IDbConnection CreateConnection() => connection;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds persistence dependencies backed by a real ServiceCollection so that
    /// GetRequiredService&lt;T&gt;() resolves correctly without NSubstitute IServiceProvider quirks.
    /// </summary>
    private static (IServiceScopeFactory ScopeFactory, IMessageRepository MessageRepo, IDelegationRepository DelegationRepo, IEventBroadcaster EventBroadcaster, List<OutboxMessage> OutboxMessages)
        BuildPersistenceDependencies()
    {
        var messageRepo = Substitute.For<IMessageRepository>();
        var delegationRepo = Substitute.For<IDelegationRepository>();
        var eventBroadcaster = Substitute.For<IEventBroadcaster>();
        var sessionRepo = Substitute.For<ISessionRepository>();
        var outboxRepo = Substitute.For<IOutboxRepository>();
        var outboxDispatcher = Substitute.For<IOutboxDispatcher>();
        var outboxMessages = new List<OutboxMessage>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        connection.BeginTransaction().Returns(transaction);
        outboxRepo.EnqueueAsync(connection, transaction, Arg.Any<OutboxMessage>()).Returns(callInfo =>
        {
            outboxMessages.Add(callInfo.ArgAt<OutboxMessage>(2));
            return 1L;
        });
        var userContext = new TestUserContext("user-1");
        var delegationService = new DelegationService(delegationRepo, eventBroadcaster, userContext);
        var connectionFactory = new StubConnectionFactory(connection);
        var sessionActivityWriteService = new SessionActivityWriteService(
            connectionFactory,
            messageRepo,
            delegationRepo,
            sessionRepo,
            outboxRepo,
            outboxDispatcher);

        var services = new ServiceCollection();
        services.AddSingleton(messageRepo);
        services.AddSingleton(delegationRepo);
        services.AddSingleton(eventBroadcaster);
        services.AddSingleton(sessionRepo);
        services.AddSingleton<IDbConnectionFactory>(connectionFactory);
        services.AddSingleton(outboxRepo);
        services.AddSingleton(outboxDispatcher);
        services.AddSingleton(sessionActivityWriteService);
        services.AddSingleton<IUserContext>(userContext);
        services.AddSingleton(delegationService);
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        return (scopeFactory, messageRepo, delegationRepo, eventBroadcaster, outboxMessages);
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
    private static async Task<(OpenCodeHarnessSession Instance, IMessageRepository MessageRepo)>
        CreateInstanceWithSseLines(
            string fleetSessionId,
            IEnumerable<string> sseLines,
            Action<IMessageRepository>? configureRepo = null)
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

    private static async Task<(OpenCodeHarnessSession Instance, IMessageRepository MessageRepo, RecordingHttpMessageHandler Handler)>
        CreateInstanceForPromptSendAsync(
            string fleetSessionId,
            Action<IMessageRepository>? configureRepo = null)
    {
        var (scopeFactory, messageRepo, _, _, _) = BuildPersistenceDependencies();
        configureRepo?.Invoke(messageRepo);

        var handler = new RecordingHttpMessageHandler();
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
            ownerUserId: TestUserContext.DefaultUserId);

        return (instance, messageRepo, handler);
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
                repo.UpsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction?>(), Arg.Any<PersistedMessage>()).Returns(callInfo =>
                {
                    persistSignal.TrySetResult();
                    return Task.CompletedTask;
                });
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeEventsAsync(instance, cts.Token);

        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        await messageRepo.Received(1).UpsertAsync(
            Arg.Any<IDbConnection>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Is<PersistedMessage>(m =>
                m.Id == "msg-1"
                && m.SessionId == fleetSessionId
                && m.Role == "assistant"
                && m.AgentName == "loom"
                && m.PartsJson.Contains("Hello")));

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
                repo.UpsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction?>(), Arg.Any<PersistedMessage>()).Returns(callInfo =>
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

        await messageRepo.Received(1).UpsertAsync(
            Arg.Any<IDbConnection>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Is<PersistedMessage>(m =>
            m.SessionId == fleetSessionId && m.Role == "user"));

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SendPromptAsync_PersistsSyntheticUserMessageBeforeDispatch()
    {
        var fleetSessionId = "fleet-send-1";
        var persistSignal = new TaskCompletionSource();
        var (scopeFactory, messageRepo, _, _, outboxMessages) = BuildPersistenceDependencies();

        messageRepo.UpsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction?>(), Arg.Any<PersistedMessage>()).Returns(_ =>
        {
            persistSignal.TrySetResult();
            return Task.CompletedTask;
        });

        var handler = new RecordingHttpMessageHandler();
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

        await instance.SendPromptAsync("Remember this prompt", new PromptOptions { Agent = "loom" }, CancellationToken.None);

        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await messageRepo.Received(1).UpsertAsync(
            Arg.Any<IDbConnection>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Is<PersistedMessage>(m =>
                m.SessionId == fleetSessionId
                && m.Role == "user"
                && m.AgentName == "loom"
                && m.PartsJson.Contains("Remember this prompt")));

        handler.RequestPaths.ShouldContain(path => path.Contains("/session", StringComparison.Ordinal));
        handler.RequestPaths.ShouldContain(path => path.Contains("/prompt_async", StringComparison.Ordinal));

        var promptPayload = JsonSerializer.Deserialize<JsonElement>(outboxMessages.Last(message => message.Type == "message.updated").Payload);
        promptPayload.GetProperty("parts")[0].GetProperty("text").GetString().ShouldBe("Remember this prompt");
    }

    [Fact]
    public async Task SendPromptAsync_PersistenceFailure_DoesNotBlockPromptDispatch()
    {
        var fleetSessionId = "fleet-send-2";

        var (instance, messageRepo, handler) = await CreateInstanceForPromptSendAsync(
            fleetSessionId,
            repo =>
            {
                repo.UpsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction?>(), Arg.Any<PersistedMessage>())
                    .Returns(_ => Task.FromException(new InvalidOperationException("db down")));
            });

        await instance.SendPromptAsync("Continue anyway", null, CancellationToken.None);

        await messageRepo.Received(1).UpsertAsync(
            Arg.Any<IDbConnection>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Any<PersistedMessage>());
        handler.RequestPaths.ShouldContain(path => path.Contains("/prompt_async", StringComparison.Ordinal));

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SendCommandAsync_PersistsSyntheticUserMessageBeforeDispatch()
    {
        var fleetSessionId = "fleet-command-1";
        var persistSignal = new TaskCompletionSource();
        var (scopeFactory, messageRepo, _, _, outboxMessages) = BuildPersistenceDependencies();

        messageRepo.UpsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction?>(), Arg.Any<PersistedMessage>()).Returns(_ =>
        {
            persistSignal.TrySetResult();
            return Task.CompletedTask;
        });

        var handler = new RecordingHttpMessageHandler();
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

        await instance.SendCommandAsync(
            new CommandOptions
            {
                Command = "review",
                Arguments = "line one\nline two",
                Agent = "loom",
                ModelId = "anthropic/claude"
            },
            CancellationToken.None);

        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await messageRepo.Received(1).UpsertAsync(
            Arg.Any<IDbConnection>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Is<PersistedMessage>(m =>
                m.SessionId == fleetSessionId
                && m.Role == "user"
                && m.AgentName == "loom"
                && m.PartsJson.Contains("/review line one line two")));

        handler.RequestPaths.ShouldContain(path => path.Contains("/session", StringComparison.Ordinal));
        handler.RequestPaths.ShouldContain(path => path.Contains("/command", StringComparison.Ordinal));

        var commandPayload = JsonSerializer.Deserialize<JsonElement>(outboxMessages.Last(message => message.Type == "message.updated").Payload);
        commandPayload.GetProperty("parts")[0].GetProperty("text").GetString().ShouldBe("/review line one line two");
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

        await messageRepo.DidNotReceive().UpsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction?>(), Arg.Any<PersistedMessage>());

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
                repo.UpsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction?>(), Arg.Any<PersistedMessage>())
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
                repo.GetByIdAsync(messageId, fleetSessionId)
                    .Returns(Task.FromResult<PersistedMessage?>(null));

                repo.UpsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction?>(), Arg.Any<PersistedMessage>()).Returns(callInfo =>
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

        await messageRepo.Received(1).UpsertAsync(
            Arg.Any<IDbConnection>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Is<PersistedMessage>(m =>
            m.Id == messageId &&
            m.SessionId == fleetSessionId &&
            m.Role == "assistant" &&
            m.PartsJson.Contains("Hello from part")));

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
                repo.GetByIdAsync(messageId, fleetSessionId)
                    .Returns(callInfo => Task.FromResult<PersistedMessage?>(lastUpserted));

                repo.UpsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction?>(), Arg.Any<PersistedMessage>()).Returns(callInfo =>
                {
                    lastUpserted = callInfo.ArgAt<PersistedMessage>(2);
                    persistSignal.TrySetResult();
                    return Task.CompletedTask;
                });
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
        await messageRepo.DidNotReceive().UpsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction?>(), Arg.Any<PersistedMessage>());

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

                repo.UpsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction?>(), Arg.Any<PersistedMessage>()).Returns(callInfo =>
                {
                    lastUpserted = callInfo.ArgAt<PersistedMessage>(2);
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
                repo.GetByIdAsync(messageId, fleetSessionId)
                    .Returns(callInfo => Task.FromResult(lastUpserted));

                repo.UpsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction?>(), Arg.Any<PersistedMessage>()).Returns(callInfo =>
                {
                    lastUpserted = callInfo.ArgAt<PersistedMessage>(2);
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
        var (scopeFactory, messageRepo, _, _, outboxMessages) = BuildPersistenceDependencies();

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

        messageRepo.GetByIdAsync(messageId, fleetSessionId)
            .Returns(_ => Task.FromResult(lastUpserted));
        messageRepo.UpsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction?>(), Arg.Any<PersistedMessage>()).Returns(callInfo =>
        {
            lastUpserted = callInfo.ArgAt<PersistedMessage>(2);
            upsertSignal.TrySetResult();
            return Task.CompletedTask;
        });

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
        var (scopeFactory, messageRepo, _, _, outboxMessages) = BuildPersistenceDependencies();

        messageRepo.GetByIdAsync(messageId, fleetSessionId)
            .Returns(Task.FromResult<PersistedMessage?>(null));
        messageRepo.UpsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction?>(), Arg.Any<PersistedMessage>()).Returns(_ =>
        {
            persistSignal.TrySetResult();
            return Task.CompletedTask;
        });

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

        await messageRepo.Received(1).UpsertAsync(
            Arg.Any<IDbConnection>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Is<PersistedMessage>(m =>
                m.Id == messageId
                && m.SessionId == fleetSessionId
                && m.Role == "assistant"
                && !m.PartsJson.Contains("Hidden thought", StringComparison.Ordinal)));

        outboxMessages.ShouldNotContain(message =>
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
                repo.GetByIdAsync(messageId, fleetSessionId)
                    .Returns(Task.FromResult<PersistedMessage?>(null));

                repo.UpsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction?>(), Arg.Any<PersistedMessage>()).Returns(_ =>
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

        await messageRepo.Received(1).UpsertAsync(
            Arg.Any<IDbConnection>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Is<PersistedMessage>(m =>
                m.Id == messageId
                && m.SessionId == fleetSessionId
                && m.PartsJson.Contains("diagram.png")
                && m.PartsJson.Contains("https://example.test/diagram.png")));

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
                repo.GetByIdAsync(messageId, fleetSessionId)
                    .Returns(Task.FromResult<PersistedMessage?>(null));

                repo.UpsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction?>(), Arg.Any<PersistedMessage>()).Returns(_ =>
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

        await messageRepo.Received(1).UpsertAsync(
            Arg.Any<IDbConnection>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Is<PersistedMessage>(m =>
                m.Id == messageId
                && m.SessionId == fleetSessionId
                && m.PartsJson.Contains("step-finish")
                && m.PartsJson.Contains("completed")));

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

        await delegationRepo.Received(1).InsertAsync(Arg.Is<Delegation>(d =>
            d.ParentSessionId == "fleet-delegation-1" &&
            d.ParentToolCallId == "tool-1" &&
            d.Title == "reviewer" &&
            d.Status == "pending"));

        await eventBroadcaster.Received(1).BroadcastAsync(
            "session:fleet-delegation-1",
            "delegation.created",
            Arg.Any<object>(),
            Arg.Any<string?>(),
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
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = "user-1"
        });
        Session? persistedChildSession = null;
        sessionRepo.GetByHarnessIdAsync("child-1").Returns(_ => persistedChildSession);
        var harnessRegistry = Substitute.For<IHarnessRegistry>();
        var harness = Substitute.For<IHarness>();
        var harnessRuntime = Substitute.For<IHarnessRuntime>();
        harness.Capabilities.Returns(new HarnessCapabilities { SupportsResume = true });
        var childHarnessInstance = Substitute.For<IHarnessSession>();
        childHarnessInstance.InstanceId.Returns("inst-child");
        childHarnessInstance.HarnessType.Returns("opencode");
        childHarnessInstance.Status.Returns(HarnessSessionStatus.Running);
        harnessRuntime.ResumeAsync(Arg.Any<HarnessResumeOptions>(), Arg.Any<CancellationToken>()).Returns(childHarnessInstance);
        harnessRegistry.GetByType("opencode").Returns(harness);
        harnessRegistry.GetRuntimeByType("opencode").Returns(harnessRuntime);
        var instanceRepo = Substitute.For<IInstanceRepository>();
        instanceRepo.InsertAsync(Arg.Any<Instance>()).Returns(Task.CompletedTask);
        sessionRepo.InsertAsync(Arg.Any<Session>())
            .Returns(callInfo =>
            {
                persistedChildSession = callInfo.Arg<Session>();
                return Task.CompletedTask;
            });
        var projectRepo = Substitute.For<IProjectRepository>();
        projectRepo.ListAsync().Returns(new List<Project>());
        var callbackRepo = Substitute.For<ISessionCallbackRepository>();
        var workspaceRepo = Substitute.For<IWorkspaceRepository>();
        var workspaceRootRepo = Substitute.For<IWorkspaceRootRepository>();
        var analyticsCollector = Substitute.For<IAnalyticsCollector>();
        var sessionSourceUsageRepo = Substitute.For<ISessionSourceUsageRepository>();
        var credentialStore = Substitute.For<ICredentialStore>();
        credentialStore.GetDecryptedCredentialsAsync(Arg.Any<string>()).Returns([]);
        harnessRuntime.PrepareRuntimeAsync(Arg.Any<RuntimePreparationContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new StubLaunchArtifacts())));
        workspaceRootRepo.ListAsync().Returns([
            new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = DateTime.UtcNow.ToString("O") }
        ]);
        var userContext = new TestUserContext("user-1");
        var workspaceRootService = new WorkspaceRootService(workspaceRootRepo, userContext);
        var options = new FleetOptions();

        var services = new ServiceCollection();
        services.AddSingleton(messageRepo);
        services.AddSingleton(delegationRepo);
        services.AddSingleton(eventBroadcaster);
        services.AddSingleton(sessionRepo);
        services.AddSingleton(instanceRepo);
        services.AddSingleton(projectRepo);
        services.AddSingleton(callbackRepo);
        services.AddSingleton(workspaceRepo);
        services.AddSingleton(sessionSourceUsageRepo);
        services.AddSingleton<IHarnessRegistry>(harnessRegistry);
        services.AddSingleton(analyticsCollector);
        services.AddSingleton(new InstanceTracker());
        services.AddSingleton<IUserContext>(userContext);
        services.AddSingleton(new InstanceService(instanceRepo, sessionRepo, userContext));
        services.AddSingleton(new DelegationService(delegationRepo, eventBroadcaster, userContext));
        services.AddSingleton(new SessionSourceResolutionService([
            new LocalDirectorySessionSourceProvider(workspaceRootService)
        ]));
        services.AddSingleton(new SessionOrchestrator(
            new WorkspaceService(workspaceRepo, userContext, options, NullLogger<WorkspaceService>.Instance),
            new InstanceService(instanceRepo, sessionRepo, userContext),
            new SessionSourceResolutionService([
                new LocalDirectorySessionSourceProvider(workspaceRootService)
            ]),
            harnessRegistry,
            new InstanceTracker(),
            sessionRepo,
            sessionSourceUsageRepo,
            callbackRepo,
            delegationRepo,
            projectRepo,
            eventBroadcaster,
            analyticsCollector,
            messageRepo,
            new DelegationService(delegationRepo, eventBroadcaster, userContext),
            credentialStore,
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

        await delegationRepo.Received(1).UpdateChildSessionIdAsync(
            "del-1",
            Arg.Any<string>(),
            Arg.Any<string>());

        await eventBroadcaster.Received().BroadcastAsync(
            "session:fleet-delegation-1",
            "delegation.updated",
            Arg.Any<object>(),
            Arg.Any<string?>(),
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
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = "user-1"
        });
        Session? persistedChildSession = null;
        sessionRepo.GetByHarnessIdAsync("child-1").Returns(_ => persistedChildSession);
        var harnessRegistry = Substitute.For<IHarnessRegistry>();
        var harness = Substitute.For<IHarness>();
        var harnessRuntime = Substitute.For<IHarnessRuntime>();
        harness.Capabilities.Returns(new HarnessCapabilities { SupportsResume = true });
        var childHarnessInstance = Substitute.For<IHarnessSession>();
        childHarnessInstance.InstanceId.Returns("inst-child");
        childHarnessInstance.HarnessType.Returns("opencode");
        childHarnessInstance.Status.Returns(HarnessSessionStatus.Running);
        harnessRuntime.ResumeAsync(Arg.Any<HarnessResumeOptions>(), Arg.Any<CancellationToken>()).Returns(childHarnessInstance);
        harnessRegistry.GetByType("opencode").Returns(harness);
        harnessRegistry.GetRuntimeByType("opencode").Returns(harnessRuntime);
        var instanceRepo = Substitute.For<IInstanceRepository>();
        instanceRepo.InsertAsync(Arg.Any<Instance>()).Returns(Task.CompletedTask);
        sessionRepo.InsertAsync(Arg.Any<Session>())
            .Returns(callInfo =>
            {
                persistedChildSession = callInfo.Arg<Session>();
                return Task.CompletedTask;
            });
        var projectRepo = Substitute.For<IProjectRepository>();
        projectRepo.ListAsync().Returns(new List<Project>());
        var callbackRepo = Substitute.For<ISessionCallbackRepository>();
        var workspaceRepo = Substitute.For<IWorkspaceRepository>();
        var analyticsCollector = Substitute.For<IAnalyticsCollector>();
        var sessionSourceUsageRepo = Substitute.For<ISessionSourceUsageRepository>();
        var credentialStore = Substitute.For<ICredentialStore>();
        credentialStore.GetDecryptedCredentialsAsync(Arg.Any<string>()).Returns([]);
        harnessRuntime.PrepareRuntimeAsync(Arg.Any<RuntimePreparationContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new StubLaunchArtifacts())));

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
        services.AddSingleton(sessionSourceUsageRepo);
        services.AddSingleton<IHarnessRegistry>(harnessRegistry);
        services.AddSingleton(analyticsCollector);
        services.AddSingleton(new InstanceTracker());
        var userContext = new TestUserContext("user-1");
        var options = new FleetOptions();
        services.AddSingleton<IUserContext>(userContext);
        services.AddSingleton(new InstanceService(instanceRepo, sessionRepo, userContext));
        services.AddSingleton(new DelegationService(delegationRepo, eventBroadcaster, userContext));
        var workspaceRootRepo = Substitute.For<IWorkspaceRootRepository>();
        workspaceRootRepo.ListAsync().Returns([
            new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = DateTime.UtcNow.ToString("O") }
        ]);
        var workspaceRootService = new WorkspaceRootService(workspaceRootRepo, userContext);
        services.AddSingleton(new SessionSourceResolutionService([
            new LocalDirectorySessionSourceProvider(workspaceRootService)
        ]));
        services.AddSingleton(new SessionOrchestrator(
            new WorkspaceService(workspaceRepo, userContext, options, NullLogger<WorkspaceService>.Instance),
            new InstanceService(instanceRepo, sessionRepo, userContext),
            new SessionSourceResolutionService([
                new LocalDirectorySessionSourceProvider(workspaceRootService)
            ]),
            harnessRegistry,
            new InstanceTracker(),
            sessionRepo,
            sessionSourceUsageRepo,
            callbackRepo,
            delegationRepo,
            projectRepo,
            eventBroadcaster,
            analyticsCollector,
            messageRepo,
            new DelegationService(delegationRepo, eventBroadcaster, userContext),
            credentialStore,
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

        await delegationRepo.Received(1).UpdateChildSessionIdAsync(
            "del-1",
            Arg.Any<string>(),
            Arg.Any<string>());

        await eventBroadcaster.Received().BroadcastAsync(
            "session:fleet-delegation-1",
            "delegation.updated",
            Arg.Any<object>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_RoutesEventsFromDelegatedChildOpenCodeSession()
    {
        var fleetSessionId = "fleet-parent";
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        connection.BeginTransaction().Returns(transaction);

        var messageRepo = Substitute.For<IMessageRepository>();
        var delegationRepo = Substitute.For<IDelegationRepository>();
        var eventBroadcaster = Substitute.For<IEventBroadcaster>();
        var sessionRepo = Substitute.For<ISessionRepository>();
        var outboxRepo = Substitute.For<IOutboxRepository>();
        var outboxDispatcher = Substitute.For<IOutboxDispatcher>();
        outboxRepo.EnqueueAsync(connection, transaction, Arg.Any<OutboxMessage>()).Returns(1L);
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
        services.AddSingleton<IDbConnectionFactory>(new StubConnectionFactory(connection));
        services.AddSingleton(outboxRepo);
        services.AddSingleton(outboxDispatcher);
        services.AddSingleton<IUserContext>(new TestUserContext("user-1"));
        services.AddSingleton(new SessionActivityWriteService(
            new StubConnectionFactory(connection),
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
        await messageRepo.Received(1).UpsertAsync(
            Arg.Any<IDbConnection>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Is<PersistedMessage>(m =>
                m.Id == "msg-child"
                && m.SessionId == "fleet-child"
                && m.PartsJson.Contains("child text")));

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
