using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Hubs;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Api.Tests.Hubs;

/// <summary>
/// Integration tests for SessionEventsHub.
/// Tests hub connectivity, subscription, snapshot delivery, and event broadcasting.
/// </summary>
#pragma warning disable CA1001 // IAsyncLifetime handles disposal via DisposeAsync
public sealed class SessionEventsHubTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private ApiWebApplicationFactory? _factory;
    private HubConnection? _connection;

    public async Task InitializeAsync()
    {
        // Configure factory with required test services
        _factory = new ApiWebApplicationFactory(
            authEnabled: false,
            configureTestServices: services =>
            {
                // Register a fake message proxy that returns empty snapshots
                services.AddSingleton<ISessionMessageProxy>(new FakeSessionMessageProxy());
            });
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task Connect_ToHub_Succeeds()
    {
        _connection = await CreateConnectedHubAsync();

        _connection.State.ShouldBe(HubConnectionState.Connected);
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
        var broadcaster = new InMemoryEventBroadcaster();
        using var factory = new ApiWebApplicationFactory(
            authEnabled: false,
            configureTestServices: services =>
            {
                services.AddSingleton<ISessionMessageProxy>(new FakeSessionMessageProxy());
                services.AddSingleton<IEventBroadcaster>(broadcaster);
            });

        var connection = await CreateConnectedHubAsync(factory);
        await using var _ = connection;

        await connection.InvokeAsync<SessionSnapshot>("SubscribeToSessionAsync", "session-1");

        var received = new TaskCompletionSource<(string Topic, long EventId)>();
        connection.On("Event", (string topic, long eventId, object? data) =>
        {
            received.TrySetResult((topic, eventId));
        });

        await WaitForSubscriberAsync(broadcaster);

        // Act
        var payload = JsonDocument.Parse("""{"text":"hello"}""").RootElement;
        await broadcaster.BroadcastAsync(
            "session:session-1", "message.created", payload, eventId: 42,
            domainEvent: null, userId: null, ct: CancellationToken.None);

        // Assert
        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        result.Topic.ShouldBe("session:session-1");
        result.EventId.ShouldBe(42);
    }

    [Fact]
    public async Task BroadcastEvent_NotDeliveredToUnsubscribedClient()
    {
        var broadcaster = new InMemoryEventBroadcaster();
        using var factory = new ApiWebApplicationFactory(
            authEnabled: false,
            configureTestServices: services =>
            {
                services.AddSingleton<ISessionMessageProxy>(new FakeSessionMessageProxy());
                services.AddSingleton<IEventBroadcaster>(broadcaster);
            });

        var connection = await CreateConnectedHubAsync(factory);
        await using var _ = connection;

        await connection.InvokeAsync<SessionSnapshot>("SubscribeToSessionAsync", "session-1");
        await connection.InvokeAsync("UnsubscribeFromSessionAsync", "session-1");

        var received = new TaskCompletionSource<bool>();
        connection.On("Event", (string topic, long eventId, object? data) =>
        {
            received.TrySetResult(true);
        });

        await WaitForSubscriberAsync(broadcaster);

        // Act
        var payload = JsonDocument.Parse("{}").RootElement;
        await broadcaster.BroadcastAsync(
            "session:session-1", "message.created", payload, eventId: 1,
            domainEvent: null, userId: null, ct: CancellationToken.None);

        // Assert: no event within 500ms
        var completed = await Task.WhenAny(received.Task, Task.Delay(500));
        completed.ShouldNotBe(received.Task, "Event was delivered after unsubscribe");
    }

    [Fact]
    public async Task BroadcastEvent_MultipleEvents_DeliveredInOrder()
    {
        var broadcaster = new InMemoryEventBroadcaster();
        using var factory = new ApiWebApplicationFactory(
            authEnabled: false,
            configureTestServices: services =>
            {
                services.AddSingleton<ISessionMessageProxy>(new FakeSessionMessageProxy());
                services.AddSingleton<IEventBroadcaster>(broadcaster);
            });

        var connection = await CreateConnectedHubAsync(factory);
        await using var _ = connection;

        await connection.InvokeAsync<SessionSnapshot>("SubscribeToSessionAsync", "session-1");

        var receivedIds = new List<long>();
        var allReceived = new TaskCompletionSource<bool>();
        connection.On("Event", (string topic, long eventId, object? data) =>
        {
            receivedIds.Add(eventId);
            if (receivedIds.Count == 3)
                allReceived.TrySetResult(true);
        });

        await WaitForSubscriberAsync(broadcaster);

        // Act
        var payload = JsonDocument.Parse("{}").RootElement;
        for (int i = 1; i <= 3; i++)
        {
            await broadcaster.BroadcastAsync(
                "session:session-1", "message.created", payload, eventId: i,
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
        => await CreateConnectedHubAsync(_factory!);

    private static async Task WaitForSubscriberAsync(InMemoryEventBroadcaster broadcaster)
    {
        var prop = broadcaster.GetType().GetProperty("SubscriberCount",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(3))
        {
            if ((int)(prop?.GetValue(broadcaster) ?? 0) > 0) return;
            await Task.Delay(25);
        }
    }

    // ── Fake Implementations ────────────────────────────────────────────────────────
}
