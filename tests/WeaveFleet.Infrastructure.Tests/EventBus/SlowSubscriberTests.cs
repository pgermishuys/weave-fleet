using System.Text.Json;
using WeaveFleet.Infrastructure.EventBus;

namespace WeaveFleet.Infrastructure.Tests.EventBus;

public sealed class SlowSubscriberTests
{
    private static readonly JsonElement Payload = JsonDocument.Parse("{}").RootElement.Clone();

    [Fact]
    public async Task a_subscriber_that_stops_reading_is_cut_off_instead_of_queueing_events_forever()
    {
        using var bus = new InMemoryEventBroadcaster(backlogLimit: 100);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var e = bus.SubscribeAsync(["*"], null, cts.Token).GetAsyncEnumerator(cts.Token);
        var first = e.MoveNextAsync().AsTask();   // subscribes
        while (bus.SubscriberCount == 0) await Task.Delay(5, cts.Token);

        for (var i = 0; i < 100_000; i++)
            await bus.BroadcastAsync("session:s1", "message.part.delta", Payload);

        // It gets what was queued before the cut, then an error it can act on.
        var read = 0;
        var cutOff = false;
        try
        {
            while (await first)
            {
                read++;
                first = e.MoveNextAsync().AsTask();
            }
        }
        catch (SubscriberFellBehindException)
        {
            cutOff = true;
        }

        cutOff.ShouldBeTrue();
        read.ShouldBeLessThan(200); // the queue held 100, plus the one a waiting reader took at once
        bus.SubscriberCount.ShouldBe(0);
    }

    [Fact]
    public async Task a_subscriber_that_keeps_up_is_never_cut_off()
    {
        using var bus = new InMemoryEventBroadcaster(backlogLimit: 100);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var received = 0;
        var reader = Task.Run(async () =>
        {
            await foreach (var _ in bus.SubscribeAsync(["*"], null, cts.Token))
                if (++received == 5_000) break;
        });
        while (bus.SubscriberCount == 0) await Task.Delay(5, cts.Token);

        // The writer never gets more than 50 events ahead of the reader (half the limit), however slow the machine is,
        // so "keeps up" is a fact of the test, not a matter of timing.
        for (var i = 0; i < 5_000; i++)
        {
            while (i - Volatile.Read(ref received) >= 50) await Task.Yield();
            await bus.BroadcastAsync("session:s1", "message.part.delta", Payload);
        }

        await reader;
        received.ShouldBe(5_000);
    }
}
