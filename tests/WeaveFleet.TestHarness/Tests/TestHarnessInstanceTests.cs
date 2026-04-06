using System.Text.Json;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.TestHarness.Tests;

public sealed class TestHarnessInstanceTests
{
    // ── Status transitions ───────────────────────────────────────────────────

    [Fact]
    public void Initial_status_is_Idle_by_default()
    {
        var scenario = new TestScenarioBuilder().Build();
        var instance = new TestHarnessInstance("inst-1", scenario);

        Assert.Equal(HarnessInstanceStatus.Idle, instance.Status);
    }

    [Fact]
    public void Initial_status_respects_scenario_configuration()
    {
        var scenario = new TestScenarioBuilder()
            .WithInitialStatus(HarnessInstanceStatus.Starting)
            .Build();
        var instance = new TestHarnessInstance("inst-1", scenario);

        Assert.Equal(HarnessInstanceStatus.Starting, instance.Status);
    }

    [Fact]
    public async Task StopAsync_transitions_to_Stopped()
    {
        var scenario = new TestScenarioBuilder().Build();
        var instance = new TestHarnessInstance("inst-1", scenario);

        await instance.StopAsync(CancellationToken.None);

        Assert.Equal(HarnessInstanceStatus.Stopped, instance.Status);
    }

    [Fact]
    public async Task AbortAsync_transitions_to_Idle()
    {
        var scenario = new TestScenarioBuilder().Build();
        var instance = new TestHarnessInstance("inst-1", scenario);

        await instance.AbortAsync(CancellationToken.None);

        Assert.Equal(HarnessInstanceStatus.Idle, instance.Status);
    }

    // ── SendPromptAsync / SubscribeAsync event flow ──────────────────────────

    [Fact]
    public async Task SendPromptAsync_queues_scenario_events_into_subscription_stream()
    {
        const string sessionId = "sess-1";
        var scenario = new TestScenarioBuilder()
            .WithSimpleTextResponse(sessionId, "msg-1", "Hello back!")
            .Build();

        var instance = new TestHarnessInstance(sessionId, scenario);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start subscription BEFORE sending prompt so we don't miss events
        var eventsTask = CollectEventsAsync(instance, expectedCount: 6, cts.Token);

        await instance.SendPromptAsync("Hello!", null, cts.Token);
        var events = await eventsTask;

        Assert.Equal(6, events.Count);
        Assert.Equal("session.status", events[0].Type);
        Assert.Equal("message.updated", events[1].Type);   // user message
        Assert.Equal("message.part.updated", events[2].Type); // user part
        Assert.Equal("message.updated", events[3].Type);   // assistant message
        Assert.Equal("message.part.updated", events[4].Type); // assistant part
        Assert.Equal("session.idle", events[5].Type);
    }

    [Fact]
    public async Task SendPromptAsync_with_no_configured_response_emits_no_events()
    {
        var scenario = new TestScenarioBuilder().Build(); // no prompt responses
        var instance = new TestHarnessInstance("sess-1", scenario);
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

        Assert.Empty(events);
    }

    [Fact]
    public async Task Multiple_prompt_responses_dequeued_in_order()
    {
        const string sessionId = "sess-multi";
        var scenario = new TestScenarioBuilder()
            .WithSimpleTextResponse(sessionId, "msg-1", "First response")
            .WithSimpleTextResponse(sessionId, "msg-2", "Second response")
            .Build();

        var instance = new TestHarnessInstance(sessionId, scenario);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Collect 12 events (6 per prompt)
        var eventsTask = CollectEventsAsync(instance, expectedCount: 12, cts.Token);

        await instance.SendPromptAsync("First prompt", null, cts.Token);

        // Wait for first batch to complete
        await Task.Delay(100, cts.Token);

        await instance.SendPromptAsync("Second prompt", null, cts.Token);
        var events = await eventsTask;

        Assert.Equal(12, events.Count);
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

        var instance = new TestHarnessInstance(sessionId, scenario);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Send prompt (fires in background)
        await instance.SendPromptAsync("Go", null, cts.Token);

        // Give it just enough time to start (enter Running state)
        await Task.Delay(50, cts.Token);

        // Abort while still in-flight
        await instance.AbortAsync(cts.Token);

        // Status should be Idle now (set by AbortAsync)
        Assert.Equal(HarnessInstanceStatus.Idle, instance.Status);
    }

    // ── GetMessagesAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetMessagesAsync_returns_scenario_messages()
    {
        var scenario = new TestScenarioBuilder()
            .WithUserMessage("msg-1", "Hello")
            .WithAssistantMessage("msg-2", "Hi there!")
            .Build();

        var instance = new TestHarnessInstance("sess-1", scenario);
        var page = await instance.GetMessagesAsync(null, CancellationToken.None);

        Assert.Equal(2, page.Messages.Count);
        Assert.Equal("msg-1", page.Messages[0].Id);
        Assert.Equal("user", page.Messages[0].Role);
        Assert.Equal("msg-2", page.Messages[1].Id);
        Assert.Equal("assistant", page.Messages[1].Role);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task GetMessagesAsync_respects_limit()
    {
        var scenario = new TestScenarioBuilder()
            .WithUserMessage("msg-1", "A")
            .WithUserMessage("msg-2", "B")
            .WithUserMessage("msg-3", "C")
            .Build();

        var instance = new TestHarnessInstance("sess-1", scenario);
        var page = await instance.GetMessagesAsync(new MessageQuery(Limit: 2), CancellationToken.None);

        Assert.Equal(2, page.Messages.Count);
        Assert.True(page.HasMore);
    }

    // ── CheckHealthAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task CheckHealthAsync_always_returns_healthy()
    {
        var scenario = new TestScenarioBuilder().Build();
        var instance = new TestHarnessInstance("sess-1", scenario);

        var result = await instance.CheckHealthAsync(CancellationToken.None);

        Assert.True(result.Healthy);
        Assert.Null(result.Message);
    }

    // ── ThrowOnSendPrompt ────────────────────────────────────────────────────

    [Fact]
    public async Task SendPromptAsync_throws_when_configured()
    {
        var scenario = new TestScenarioBuilder()
            .WithSendPromptFailure()
            .Build();

        var instance = new TestHarnessInstance("sess-1", scenario);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            instance.SendPromptAsync("test", null, CancellationToken.None));
    }

    // ── PushEventAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task PushEventAsync_delivers_event_to_subscribers()
    {
        var scenario = new TestScenarioBuilder().Build();
        var instance = new TestHarnessInstance("sess-1", scenario);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var eventsTask = CollectEventsAsync(instance, expectedCount: 1, cts.Token);

        await instance.PushEventAsync(new HarnessEvent
        {
            Type = "custom.event",
            SessionId = "sess-1",
            Timestamp = DateTimeOffset.UtcNow
        });

        var events = await eventsTask;
        Assert.Single(events);
        Assert.Equal("custom.event", events[0].Type);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<List<HarnessEvent>> CollectEventsAsync(
        TestHarnessInstance instance,
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
