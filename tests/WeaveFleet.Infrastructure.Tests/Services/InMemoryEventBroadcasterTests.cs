using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Tests.Services;

public sealed class InMemoryEventBroadcasterTests
{
    // -----------------------------------------------------------------------
    // Wildcard subscriber ["*"] tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Wildcard_subscriber_receives_event_on_arbitrary_session_topic()
    {
        using var broadcaster = new InMemoryEventBroadcaster();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var received = new List<string>();
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var evt in broadcaster.SubscribeAsync(["*"], subscriberUserId: null, cts.Token))
            {
                received.Add(evt.Topic);
                break; // stop after first event
            }
        });

        // Wait until the subscriber has registered
        while (broadcaster.SubscriberCount < 1)
            await Task.Delay(10);

        await broadcaster.BroadcastAsync("session:abc", "message.updated",
            JsonSerializer.SerializeToElement(new { text = "hello" }));

        await subscribeTask;

        received.ShouldContain("session:abc");
    }

    [Fact]
    public async Task Wildcard_subscriber_receives_event_on_named_sessions_topic()
    {
        using var broadcaster = new InMemoryEventBroadcaster();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var received = new List<string>();
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var evt in broadcaster.SubscribeAsync(["*"], subscriberUserId: null, cts.Token))
            {
                received.Add(evt.Topic);
                break;
            }
        });

        // Wait until the subscriber has registered
        while (broadcaster.SubscriberCount < 1)
            await Task.Delay(10);

        await broadcaster.BroadcastAsync("sessions", "session_created",
            JsonSerializer.SerializeToElement(new { id = "s1" }));

        await subscribeTask;

        received.ShouldContain("sessions");
    }

    // -----------------------------------------------------------------------
    // Specific-topic subscriber tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Specific_subscriber_does_not_receive_event_on_different_topic()
    {
        using var broadcaster = new InMemoryEventBroadcaster();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var received = new List<string>();
        var subscribeTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in broadcaster.SubscribeAsync(["sessions"], subscriberUserId: null, cts.Token))
                    received.Add(evt.Topic);
            }
            catch (OperationCanceledException)
            {
                // expected when test cancels
            }
        });

        // Wait until the subscriber has registered
        while (broadcaster.SubscriberCount < 1)
            await Task.Delay(10);

        // Broadcast on a different topic — should NOT be delivered to "sessions" subscriber
        await broadcaster.BroadcastAsync("session:abc", "message.updated",
            JsonSerializer.SerializeToElement(new { text = "hello" }));

        // Wait briefly then cancel — subscriber should have received nothing
        await Task.Delay(100);
        await cts.CancelAsync();
        await subscribeTask;

        received.ShouldBeEmpty();
    }

    [Fact]
    public async Task Specific_subscriber_receives_event_on_matching_topic()
    {
        using var broadcaster = new InMemoryEventBroadcaster();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var received = new List<string>();
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var evt in broadcaster.SubscribeAsync(["sessions"], subscriberUserId: null, cts.Token))
            {
                received.Add(evt.Topic);
                break;
            }
        });

        // Wait until the subscriber has registered
        while (broadcaster.SubscriberCount < 1)
            await Task.Delay(10);

        await broadcaster.BroadcastAsync("sessions", "session_created",
            JsonSerializer.SerializeToElement(new { id = "s1" }));

        await subscribeTask;

        received.ShouldContain("sessions");
    }

    [Fact]
    public async Task Activity_subscriber_receives_sessions_topic_compatibility_events()
    {
        using var broadcaster = new InMemoryEventBroadcaster();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        BroadcastEvent? received = null;
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var evt in broadcaster.SubscribeAsync(["activity"], subscriberUserId: null, cts.Token))
            {
                received = evt;
                break;
            }
        });

        while (broadcaster.SubscriberCount < 1)
            await Task.Delay(10);

        await broadcaster.BroadcastAsync(
            "sessions",
            "session_created",
            JsonSerializer.SerializeToElement(new { id = "s1" }),
            sequenceNumber: 99,
            userId: null,
            CancellationToken.None);

        await subscribeTask;

        received.ShouldNotBeNull();
        received!.Topic.ShouldBe("sessions");
        received.SequenceNumber.ShouldBe(99);
    }

    // -----------------------------------------------------------------------
    // Multiple concurrent subscriber tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Wildcard_and_specific_subscribers_coexist_correctly()
    {
        using var broadcaster = new InMemoryEventBroadcaster();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var wildcardReceived = new List<string>();
        var specificReceived = new List<string>();

        var wildcardDone = new TaskCompletionSource();
        var specificDone = new TaskCompletionSource();

        _ = Task.Run(async () =>
        {
            await foreach (var evt in broadcaster.SubscribeAsync(["*"], subscriberUserId: null, cts.Token))
            {
                wildcardReceived.Add(evt.Topic);
                wildcardDone.TrySetResult();
                break;
            }
        });

        _ = Task.Run(async () =>
        {
            await foreach (var evt in broadcaster.SubscribeAsync(["sessions"], subscriberUserId: null, cts.Token))
            {
                specificReceived.Add(evt.Topic);
                specificDone.TrySetResult();
                break;
            }
        });

        // Wait until both subscribers have registered
        while (broadcaster.SubscriberCount < 2)
            await Task.Delay(10);

        // Broadcast on "sessions" — both subscribers should get it
        await broadcaster.BroadcastAsync("sessions", "session_created",
            JsonSerializer.SerializeToElement(new { id = "s1" }));

        await Task.WhenAll(wildcardDone.Task, specificDone.Task).WaitAsync(TimeSpan.FromSeconds(3));

        wildcardReceived.ShouldContain("sessions");
        specificReceived.ShouldContain("sessions");

        await cts.CancelAsync();
    }
}
