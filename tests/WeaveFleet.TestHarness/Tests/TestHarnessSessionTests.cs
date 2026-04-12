using System.Text.Json;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.TestHarness.Tests;

public sealed class TestHarnessSessionTests
{
    // ── Status transitions ───────────────────────────────────────────────────

    [Fact]
    public void Initial_status_is_Idle_by_default()
    {
        var scenario = new TestScenarioBuilder().Build();
        var instance = new TestHarnessSession("inst-1", scenario);

        instance.Status.ShouldBe(HarnessSessionStatus.Idle);
    }

    [Fact]
    public void Initial_status_respects_scenario_configuration()
    {
        var scenario = new TestScenarioBuilder()
            .WithInitialStatus(HarnessSessionStatus.Starting)
            .Build();
        var instance = new TestHarnessSession("inst-1", scenario);

        instance.Status.ShouldBe(HarnessSessionStatus.Starting);
    }

    [Fact]
    public async Task StopAsync_transitions_to_Stopped()
    {
        var scenario = new TestScenarioBuilder().Build();
        var instance = new TestHarnessSession("inst-1", scenario);

        await instance.StopAsync(CancellationToken.None);

        instance.Status.ShouldBe(HarnessSessionStatus.Stopped);
    }

    [Fact]
    public async Task AbortAsync_transitions_to_Idle()
    {
        var scenario = new TestScenarioBuilder().Build();
        var instance = new TestHarnessSession("inst-1", scenario);

        await instance.AbortAsync(CancellationToken.None);

        instance.Status.ShouldBe(HarnessSessionStatus.Idle);
    }

    // ── SendPromptAsync / SubscribeAsync event flow ──────────────────────────

    [Fact]
    public async Task SendPromptAsync_queues_scenario_events_into_subscription_stream()
    {
        const string sessionId = "sess-1";
        var scenario = new TestScenarioBuilder()
            .WithSimpleTextResponse(sessionId, "msg-1", "Hello back!")
            .Build();

        var instance = new TestHarnessSession(sessionId, scenario);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start subscription BEFORE sending prompt so we don't miss events
        var eventsTask = CollectEventsAsync(instance, expectedCount: 6, cts.Token);

        await instance.SendPromptAsync("Hello!", null, cts.Token);
        var events = await eventsTask;

        events.Count.ShouldBe(6);
        events[0].Type.ShouldBe("session.status");
        events[1].Type.ShouldBe("message.updated");   // user message
        events[2].Type.ShouldBe("message.part.updated"); // user part
        events[3].Type.ShouldBe("message.updated");   // assistant message
        events[4].Type.ShouldBe("message.part.updated"); // assistant part
        events[5].Type.ShouldBe("session.idle");
    }

    [Fact]
    public async Task SendPromptAsync_with_no_configured_response_emits_no_events()
    {
        var scenario = new TestScenarioBuilder().Build(); // no prompt responses
        var instance = new TestHarnessSession("sess-1", scenario);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await instance.SendPromptAsync("Hi", null, CancellationToken.None);

        // Collect with timeout — should get 0 events
        var events = new List<HarnessEvent>();
        try
        {
            await foreach (var evt in instance.SubscribeAsync(cts.Token))
                events.Add(evt);
        }
        catch (OperationCanceledException) { }

        events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Multiple_prompt_responses_dequeued_in_order()
    {
        const string sessionId = "sess-multi";
        var scenario = new TestScenarioBuilder()
            .WithSimpleTextResponse(sessionId, "msg-1", "First response")
            .WithSimpleTextResponse(sessionId, "msg-2", "Second response")
            .Build();

        var instance = new TestHarnessSession(sessionId, scenario);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Send first prompt and wait for its 6 events to be fully emitted.
        // This avoids a race where the second SendPromptAsync cancels the
        // first prompt's in-flight CTS before all events are written.
        var firstEventsTask = CollectEventsAsync(instance, expectedCount: 6, cts.Token);
        await instance.SendPromptAsync("First prompt", null, cts.Token);
        var firstBatch = await firstEventsTask;
        firstBatch.Count.ShouldBe(6);

        // Now send the second prompt and collect its events
        var secondEventsTask = CollectEventsAsync(instance, expectedCount: 6, cts.Token);
        await instance.SendPromptAsync("Second prompt", null, cts.Token);
        var secondBatch = await secondEventsTask;
        secondBatch.Count.ShouldBe(6);

        // Verify both batches were dequeued in order
        var allEvents = firstBatch.Concat(secondBatch).ToList();
        allEvents.Count.ShouldBe(12);
    }

    // ── AbortAsync cancellation ──────────────────────────────────────────────

    [Fact]
    public async Task AbortAsync_transitions_status_to_Idle_during_running_prompt()
    {
        const string sessionId = "sess-abort";

        // Configure a slow response so it's still in flight when we abort
        var scenario = new TestScenarioBuilder()
            .WithPromptResponse(b => b
                .AddEvent(new HarnessEvent
                {
                    Type = "session.status",
                    SessionId = sessionId,
                    Timestamp = DateTimeOffset.UtcNow,
                    Payload = JsonSerializer.SerializeToElement(new { })
                })
                .AddEvent(new HarnessEvent
                {
                    Type = "session.idle",
                    SessionId = sessionId,
                    Timestamp = DateTimeOffset.UtcNow,
                    Payload = JsonSerializer.SerializeToElement(new { })
                }, TimeSpan.FromMilliseconds(500)) // 500ms delay — gives us time to abort
            )
            .Build();

        var instance = new TestHarnessSession(sessionId, scenario);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Send prompt (fires in background)
        await instance.SendPromptAsync("Go", null, cts.Token);

        // Give it just enough time to start (enter Running state)
        await Task.Delay(50, cts.Token);

        // Abort while still in-flight
        await instance.AbortAsync(cts.Token);

        // Status should be Idle now (set by AbortAsync)
        instance.Status.ShouldBe(HarnessSessionStatus.Idle);
    }

    // ── GetMessagesAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetMessagesAsync_returns_scenario_messages()
    {
        var scenario = new TestScenarioBuilder()
            .WithUserMessage("msg-1", "Hello")
            .WithAssistantMessage("msg-2", "Hi there!")
            .Build();

        var instance = new TestHarnessSession("sess-1", scenario);
        var page = await instance.GetMessagesAsync(null, CancellationToken.None);

        page.Messages.Count.ShouldBe(2);
        page.Messages[0].Id.ShouldBe("msg-1");
        page.Messages[0].Role.ShouldBe("user");
        page.Messages[1].Id.ShouldBe("msg-2");
        page.Messages[1].Role.ShouldBe("assistant");
        page.HasMore.ShouldBeFalse();
    }

    [Fact]
    public async Task GetMessagesAsync_respects_limit()
    {
        var scenario = new TestScenarioBuilder()
            .WithUserMessage("msg-1", "A")
            .WithUserMessage("msg-2", "B")
            .WithUserMessage("msg-3", "C")
            .Build();

        var instance = new TestHarnessSession("sess-1", scenario);
        var page = await instance.GetMessagesAsync(new MessageQuery(Limit: 2), CancellationToken.None);

        page.Messages.Count.ShouldBe(2);
        page.HasMore.ShouldBeTrue();
    }

    // ── CheckHealthAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task CheckHealthAsync_always_returns_healthy()
    {
        var scenario = new TestScenarioBuilder().Build();
        var instance = new TestHarnessSession("sess-1", scenario);

        var result = await instance.CheckHealthAsync(CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Message.ShouldBeNull();
    }

    // ── ThrowOnSendPrompt ────────────────────────────────────────────────────

    [Fact]
    public async Task SendPromptAsync_throws_when_configured()
    {
        var scenario = new TestScenarioBuilder()
            .WithSendPromptFailure()
            .Build();

        var instance = new TestHarnessSession("sess-1", scenario);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            instance.SendPromptAsync("test", null, CancellationToken.None));
    }

    // ── PushEventAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task PushEventAsync_delivers_event_to_subscribers()
    {
        var scenario = new TestScenarioBuilder().Build();
        var instance = new TestHarnessSession("sess-1", scenario);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var eventsTask = CollectEventsAsync(instance, expectedCount: 1, cts.Token);

        await instance.PushEventAsync(new HarnessEvent
        {
            Type = "custom.event",
            SessionId = "sess-1",
            Timestamp = DateTimeOffset.UtcNow
        });

        var events = await eventsTask;
        events.Count.ShouldBe(1);
        events[0].Type.ShouldBe("custom.event");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<List<HarnessEvent>> CollectEventsAsync(
        TestHarnessSession instance,
        int expectedCount,
        CancellationToken ct)
    {
        var events = new List<HarnessEvent>();
        await foreach (var evt in instance.SubscribeAsync(ct))
        {
            events.Add(evt);
            if (events.Count >= expectedCount)
                break;
        }
        return events;
    }
}

