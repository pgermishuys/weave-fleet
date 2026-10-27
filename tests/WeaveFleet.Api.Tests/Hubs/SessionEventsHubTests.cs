using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Hubs;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.EventBus;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Api.Tests.Hubs;

/// <summary>
/// Integration tests for SessionEventsHub.
/// Tests hub connectivity, subscription, snapshot delivery, and event broadcasting.
/// </summary>
#pragma warning disable CA1001 // IAsyncLifetime handles disposal via DisposeAsync
public sealed class SessionEventsHubTests : IClassFixture<SessionEventsHubTests.Server>, IClassFixture<SessionEventsHubTests.BroadcastServer>, IAsyncLifetime
#pragma warning restore CA1001
{
    private readonly Server _server;
    private readonly BroadcastServer _broadcastServer;
    private HubConnection? _connection;
    private FakeSessionMessageProxy _proxy => _server.Proxy;
    private InMemorySessionContextRepository _contexts => _server.Contexts;
    private ApiWebApplicationFactory _factory => _server.Factory;

    public SessionEventsHubTests(Server server, BroadcastServer broadcastServer)
    {
        _server = server;
        _broadcastServer = broadcastServer;
        _proxy.GetSnapshotBehavior = null;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task Connect_ToHub_Succeeds()
    {
        _connection = await CreateConnectedHubAsync();

        _connection.State.ShouldBe(HubConnectionState.Connected);
    }

    // Scrolling to the top of a long conversation loads the page before the snapshot's cursor.
    [Fact]
    public async Task LoadHistory_ReturnsThePageBeforeTheCursor()
    {
        string? requestedCursor = null;
        _proxy.GetSnapshotBehavior = (sessionId, _, cursor, _) =>
        {
            requestedCursor = cursor;
            return Task.FromResult(new SessionSnapshot
            {
                Session = new SessionSnapshotSession { Id = sessionId, Title = "Long", Status = "active" },
                Messages =
                [
                    new MessageLifecyclePayload
                    {
                        Info = new MessageEventInfo
                        {
                            Id = "msg_old",
                            Role = "user",
                            SessionId = sessionId,
                            Time = new MessageEventTime { Created = 1 },
                        },
                        Parts = [],
                    },
                ],
                ActivityStatus = "idle",
                HasMore = true,
                Cursor = "cursor-oldest",
            });
        };
        _connection = await CreateConnectedHubAsync();

        var page = await _connection.InvokeAsync<SessionHistoryPage>("LoadHistoryAsync", "session-1", "cursor-older");

        requestedCursor.ShouldBe("cursor-older");
        page.Messages.ShouldHaveSingleItem().Info.Id.ShouldBe("msg_old");
        page.HasMore.ShouldBeTrue();
        page.Cursor.ShouldBe("cursor-oldest");
    }

    [Fact]
    public async Task SubscribeToSession_ReturnsSnapshot()
    {
        _connection = await CreateConnectedHubAsync();

        var snapshot = await _connection.InvokeAsync<SessionSnapshot>("SubscribeToSessionAsync", "session-1");

        snapshot.ShouldNotBeNull();
        snapshot.Session.ShouldNotBeNull();
        snapshot.Session.Id.ShouldBe("session-1");
    }

    // The ring by Send has the context's size as soon as the session opens, and again after a reconnect.
    [Fact]
    public async Task SubscribeToSession_IncludesHowFullTheContextIs()
    {
        var sessionId = $"session-{Guid.NewGuid():N}";
        await _contexts.UpsertAsync(new SessionContext
        {
            SessionId = sessionId,
            UserId = "local-user",
            Used = 76_000,
            Limit = 200_000,
            ModelId = "claude-opus-5",
            LastCall = new ContextCall { Input = 1_000, CacheRead = 75_000 },
            Turns = [new SessionContextTurn(76_000, 200_000, DateTimeOffset.UnixEpoch, AfterCompaction: false)],
        }, CancellationToken.None);
        _connection = await CreateConnectedHubAsync();

        var snapshot = await _connection.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        var context = snapshot.GetProperty("context");
        context.GetProperty("used").GetInt32().ShouldBe(76_000);
        context.GetProperty("limit").GetInt32().ShouldBe(200_000);
        context.GetProperty("lastCall").GetProperty("cacheRead").GetInt32().ShouldBe(75_000);
        context.GetProperty("turns")[0].GetProperty("used").GetInt32().ShouldBe(76_000);
    }

    [Fact]
    public async Task UnsubscribeFromSession_Succeeds()
    {
        _connection = await CreateConnectedHubAsync();

        // Subscribe then unsubscribe
        await _connection.InvokeAsync<SessionSnapshot>("SubscribeToSessionAsync", "session-1");
        await _connection.InvokeAsync("UnsubscribeFromSessionAsync", "session-1");

        // No exception means success
        _connection.State.ShouldBe(HubConnectionState.Connected);
    }

    [Fact]
    public async Task Disconnect_CleansUpResources()
    {
        _connection = await CreateConnectedHubAsync();
        await _connection.InvokeAsync<SessionSnapshot>("SubscribeToSessionAsync", "session-1");

        await _connection.StopAsync();

        _connection.State.ShouldBe(HubConnectionState.Disconnected);
    }

    [Fact]
    public async Task BroadcastEvent_DeliveredToSubscribedClient()
    {
        // Arrange: use the real InMemoryEventBroadcaster so the pump delivers events
        var broadcaster = _broadcastServer.Broadcaster;
        var sessionId = $"session-{Guid.NewGuid():N}";
        var topic = $"session:{sessionId}";
        var baseline = SubscriberCount(broadcaster);

        var connection = await CreateConnectedHubAsync(_broadcastServer.Factory);
        await using var _ = connection;

        await connection.InvokeAsync<SessionSnapshot>("SubscribeToSessionAsync", sessionId);

        var received = new TaskCompletionSource<(string Topic, long EventId)>();
        connection.On("Event", (string evtTopic, long eventId, object? data) =>
        {
            received.TrySetResult((evtTopic, eventId));
        });

        await WaitForSubscriberAsync(broadcaster, baseline);

        // Act
        var payload = JsonDocument.Parse("""{"text":"hello"}""").RootElement;
        await broadcaster.BroadcastAsync(
            topic, "message.created", payload, eventId: 42,
            domainEvent: null, userId: null, ct: CancellationToken.None);

        // Assert
        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        result.Topic.ShouldBe(topic);
        result.EventId.ShouldBe(42);
    }

    [Fact]
    public async Task BroadcastEvent_NotDeliveredToUnsubscribedClient()
    {
        var broadcaster = _broadcastServer.Broadcaster;
        var sessionId = $"session-{Guid.NewGuid():N}";
        var topic = $"session:{sessionId}";
        var baseline = SubscriberCount(broadcaster);

        var connection = await CreateConnectedHubAsync(_broadcastServer.Factory);
        await using var _ = connection;

        await connection.InvokeAsync<SessionSnapshot>("SubscribeToSessionAsync", sessionId);
        await connection.InvokeAsync("UnsubscribeFromSessionAsync", sessionId);

        var received = new TaskCompletionSource<bool>();
        connection.On("Event", (string evtTopic, long eventId, object? data) =>
        {
            received.TrySetResult(true);
        });

        await WaitForSubscriberAsync(broadcaster, baseline);

        // Act
        var payload = JsonDocument.Parse("{}").RootElement;
        await broadcaster.BroadcastAsync(
            topic, "message.created", payload, eventId: 1,
            domainEvent: null, userId: null, ct: CancellationToken.None);

        // Assert: no event within 500ms
        var completed = await Task.WhenAny(received.Task, Task.Delay(500));
        completed.ShouldNotBe(received.Task, "Event was delivered after unsubscribe");
    }

    [Fact]
    public async Task BroadcastEvent_MultipleEvents_DeliveredInOrder()
    {
        var broadcaster = _broadcastServer.Broadcaster;
        var sessionId = $"session-{Guid.NewGuid():N}";
        var topic = $"session:{sessionId}";
        var baseline = SubscriberCount(broadcaster);

        var connection = await CreateConnectedHubAsync(_broadcastServer.Factory);
        await using var _ = connection;

        await connection.InvokeAsync<SessionSnapshot>("SubscribeToSessionAsync", sessionId);

        var receivedIds = new List<long>();
        var allReceived = new TaskCompletionSource<bool>();
        connection.On("Event", (string evtTopic, long eventId, object? data) =>
        {
            receivedIds.Add(eventId);
            if (receivedIds.Count == 3)
                allReceived.TrySetResult(true);
        });

        await WaitForSubscriberAsync(broadcaster, baseline);

        // Act
        var payload = JsonDocument.Parse("{}").RootElement;
        for (int i = 1; i <= 3; i++)
        {
            await broadcaster.BroadcastAsync(
                topic, "message.created", payload, eventId: i,
                domainEvent: null, userId: null, ct: CancellationToken.None);
        }

        // Assert
        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        receivedIds.ShouldBe([1, 2, 3]);
    }

    // ── Test Helpers ────────────────────────────────────────────────────────────────

    private static async Task<HubConnection> CreateConnectedHubAsync(ApiWebApplicationFactory factory)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"{factory.Server.BaseAddress}hubs/session-events", options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .AddJsonProtocol()
            .Build();

        await connection.StartAsync();
        return connection;
    }

    private async Task<HubConnection> CreateConnectedHubAsync()
        => await CreateConnectedHubAsync(_factory);

    private static int SubscriberCount(InMemoryEventBroadcaster broadcaster)
    {
        var prop = broadcaster.GetType().GetProperty("SubscriberCount",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return (int)(prop?.GetValue(broadcaster) ?? 0);
    }

    // The server is shared, so wait for this connection's pump: the count rising above what it was before connecting.
    private static async Task WaitForSubscriberAsync(InMemoryEventBroadcaster broadcaster, int baseline)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (SubscriberCount(broadcaster) > baseline) return;
            await Task.Delay(25);
        }
    }

    // A fake message proxy that returns empty snapshots, and a context repository the tests can fill.
    public sealed class Server : IDisposable
    {
        public FakeSessionMessageProxy Proxy { get; } = new();
        public InMemorySessionContextRepository Contexts { get; } = new();
        public ApiWebApplicationFactory Factory { get; }

        public Server()
        {
            Factory = new ApiWebApplicationFactory(
                authEnabled: false,
                configureTestServices: services =>
                {
                    services.AddSingleton<ISessionMessageProxy>(Proxy);
                    services.AddSingleton<ISessionContextRepository>(Contexts);
                });
        }

        public void Dispose() => Factory.Dispose();
    }

    // The real in-memory broadcaster, so the pump delivers events the tests broadcast.
    public sealed class BroadcastServer : IDisposable
    {
        public InMemoryEventBroadcaster Broadcaster { get; } = new();
        public ApiWebApplicationFactory Factory { get; }

        public BroadcastServer()
        {
            Factory = new ApiWebApplicationFactory(
                authEnabled: false,
                configureTestServices: services =>
                {
                    services.AddSingleton<ISessionMessageProxy>(new FakeSessionMessageProxy());
                    services.AddSingleton<IEventBroadcaster>(Broadcaster);
                });
        }

        public void Dispose() => Factory.Dispose();
    }
}
