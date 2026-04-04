using System.Text.Json;
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
            await foreach (var evt in broadcaster.SubscribeAsync(["*"], cts.Token))
            {
                received.Add(evt.Topic);
                break; // stop after first event
            }
        });

        // Give the subscriber time to register
        await Task.Delay(50);

        await broadcaster.BroadcastAsync("session:abc", "message.updated",
            JsonSerializer.SerializeToElement(new { text = "hello" }));

        await subscribeTask;

        Assert.Contains("session:abc", received);
    }

    [Fact]
    public async Task Wildcard_subscriber_receives_event_on_named_sessions_topic()
    {
        using var broadcaster = new InMemoryEventBroadcaster();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var received = new List<string>();
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var evt in broadcaster.SubscribeAsync(["*"], cts.Token))
            {
                received.Add(evt.Topic);
                break;
            }
        });

        await Task.Delay(50);

        await broadcaster.BroadcastAsync("sessions", "session_created",
            JsonSerializer.SerializeToElement(new { id = "s1" }));

        await subscribeTask;

        Assert.Contains("sessions", received);
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
                await foreach (var evt in broadcaster.SubscribeAsync(["sessions"], cts.Token))
                    received.Add(evt.Topic);
            }
            catch (OperationCanceledException)
            {
                // expected when test cancels
            }
        });

        await Task.Delay(50);

        // Broadcast on a different topic — should NOT be delivered to "sessions" subscriber
        await broadcaster.BroadcastAsync("session:abc", "message.updated",
            JsonSerializer.SerializeToElement(new { text = "hello" }));

        // Wait briefly then cancel — subscriber should have received nothing
        await Task.Delay(100);
        await cts.CancelAsync();
        await subscribeTask;

        Assert.Empty(received);
    }

    [Fact]
    public async Task Specific_subscriber_receives_event_on_matching_topic()
    {
        using var broadcaster = new InMemoryEventBroadcaster();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var received = new List<string>();
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var evt in broadcaster.SubscribeAsync(["sessions"], cts.Token))
            {
                received.Add(evt.Topic);
                break;
            }
        });

        await Task.Delay(50);

        await broadcaster.BroadcastAsync("sessions", "session_created",
            JsonSerializer.SerializeToElement(new { id = "s1" }));

        await subscribeTask;

        Assert.Contains("sessions", received);
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
            await foreach (var evt in broadcaster.SubscribeAsync(["*"], cts.Token))
            {
                wildcardReceived.Add(evt.Topic);
                wildcardDone.TrySetResult();
                break;
            }
        });

        _ = Task.Run(async () =>
        {
            await foreach (var evt in broadcaster.SubscribeAsync(["sessions"], cts.Token))
            {
                specificReceived.Add(evt.Topic);
                specificDone.TrySetResult();
                break;
            }
        });

        await Task.Delay(50);

        // Broadcast on "sessions" — both subscribers should get it
        await broadcaster.BroadcastAsync("sessions", "session_created",
            JsonSerializer.SerializeToElement(new { id = "s1" }));

        await Task.WhenAll(wildcardDone.Task, specificDone.Task).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Contains("sessions", wildcardReceived);
        Assert.Contains("sessions", specificReceived);

        await cts.CancelAsync();
    }
}
