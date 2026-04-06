using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.TestHarness.Tests;

public sealed class TestScenarioBuilderTests
{
    [Fact]
    public void Default_build_produces_empty_scenario()
    {
        var scenario = new TestScenarioBuilder().Build();

        Assert.Empty(scenario.Messages);
        Assert.Empty(scenario.PromptResponses);
        Assert.False(scenario.ThrowOnSpawn);
        Assert.False(scenario.ThrowOnSendPrompt);
        Assert.Equal(HarnessInstanceStatus.Idle, scenario.InitialStatus);
    }

    [Fact]
    public void WithUserMessage_adds_user_message()
    {
        var scenario = new TestScenarioBuilder()
            .WithUserMessage("msg-1", "Hello world")
            .Build();

        Assert.Single(scenario.Messages);
        var msg = scenario.Messages[0];
        Assert.Equal("msg-1", msg.Id);
        Assert.Equal("user", msg.Role);
        Assert.Equal("Hello world", msg.TextContent);
        var part = Assert.IsType<TextPart>(msg.Parts[0]);
        Assert.Equal("Hello world", part.Text);
    }

    [Fact]
    public void WithAssistantMessage_adds_assistant_message()
    {
        var scenario = new TestScenarioBuilder()
            .WithAssistantMessage("msg-2", "Response text")
            .Build();

        Assert.Single(scenario.Messages);
        var msg = scenario.Messages[0];
        Assert.Equal("assistant", msg.Role);
        Assert.Equal("Response text", msg.TextContent);
    }

    [Fact]
    public void WithAssistantMessageParts_adds_custom_parts()
    {
        var parts = new MessagePart[]
        {
            new TextPart("intro"),
            new ToolUsePart("call-1", "bash", default, ToolUseState.Completed)
        };

        var scenario = new TestScenarioBuilder()
            .WithAssistantMessageParts("msg-3", parts)
            .Build();

        Assert.Single(scenario.Messages);
        var msg = scenario.Messages[0];
        Assert.Equal(2, msg.Parts.Count);
        Assert.IsType<TextPart>(msg.Parts[0]);
        Assert.IsType<ToolUsePart>(msg.Parts[1]);
    }

    [Fact]
    public void Multiple_messages_preserved_in_order()
    {
        var scenario = new TestScenarioBuilder()
            .WithUserMessage("u1", "First")
            .WithAssistantMessage("a1", "Second")
            .WithUserMessage("u2", "Third")
            .Build();

        Assert.Equal(3, scenario.Messages.Count);
        Assert.Equal("u1", scenario.Messages[0].Id);
        Assert.Equal("a1", scenario.Messages[1].Id);
        Assert.Equal("u2", scenario.Messages[2].Id);
    }

    [Fact]
    public void WithSimpleTextResponse_enqueues_4_events()
    {
        var scenario = new TestScenarioBuilder()
            .WithSimpleTextResponse("sess-1", "msg-1", "Hello back!")
            .Build();

        Assert.Single(scenario.PromptResponses);
        var events = scenario.PromptResponses.Dequeue();
        Assert.Equal(4, events.Count);
        Assert.Equal("session.busy", events[0].Event.Type);
        Assert.Equal("message.updated", events[1].Event.Type);
        Assert.Equal("message.part.updated", events[2].Event.Type);
        Assert.Equal("session.idle", events[3].Event.Type);
    }

    [Fact]
    public void WithPromptResponse_enqueues_custom_events()
    {
        var scenario = new TestScenarioBuilder()
            .WithPromptResponse(b => b
                .AddEvent(new HarnessEvent
                {
                    Type = "custom.event",
                    SessionId = "sess-1",
                    Timestamp = DateTimeOffset.UtcNow
                })
            )
            .Build();

        Assert.Single(scenario.PromptResponses);
        var events = scenario.PromptResponses.Dequeue();
        Assert.Single(events);
        Assert.Equal("custom.event", events[0].Event.Type);
    }

    [Fact]
    public void Multiple_prompt_responses_enqueued_in_order()
    {
        var scenario = new TestScenarioBuilder()
            .WithSimpleTextResponse("sess-1", "msg-1", "First")
            .WithSimpleTextResponse("sess-1", "msg-2", "Second")
            .Build();

        Assert.Equal(2, scenario.PromptResponses.Count);
        var first = scenario.PromptResponses.Dequeue();
        Assert.Equal("session.busy", first[0].Event.Type);
        var second = scenario.PromptResponses.Dequeue();
        Assert.Equal("session.busy", second[0].Event.Type);
    }

    [Fact]
    public void WithSpawnFailure_sets_ThrowOnSpawn()
    {
        var scenario = new TestScenarioBuilder()
            .WithSpawnFailure()
            .Build();

        Assert.True(scenario.ThrowOnSpawn);
        Assert.False(scenario.ThrowOnSendPrompt);
    }

    [Fact]
    public void WithSendPromptFailure_sets_ThrowOnSendPrompt()
    {
        var scenario = new TestScenarioBuilder()
            .WithSendPromptFailure()
            .Build();

        Assert.False(scenario.ThrowOnSpawn);
        Assert.True(scenario.ThrowOnSendPrompt);
    }

    [Fact]
    public void WithInitialStatus_sets_status()
    {
        var scenario = new TestScenarioBuilder()
            .WithInitialStatus(HarnessInstanceStatus.Running)
            .Build();

        Assert.Equal(HarnessInstanceStatus.Running, scenario.InitialStatus);
    }

    // ── TestHarness integration ──────────────────────────────────────────────

    [Fact]
    public async Task TestHarness_Configure_fluent_sets_scenario()
    {
        var harness = new TestHarness();
        harness.Configure(b => b.WithUserMessage("msg-1", "Test message"));

        var instance = (TestHarnessInstance)await harness.SpawnAsync(
            new WeaveFleet.Application.Harnesses.HarnessSpawnOptions
            {
                SessionId = "sess-1",
                WorkingDirectory = "/tmp"
            },
            CancellationToken.None);

        var page = await instance.GetMessagesAsync(null, CancellationToken.None);
        Assert.Single(page.Messages);
        Assert.Equal("msg-1", page.Messages[0].Id);
    }

    [Fact]
    public async Task TestHarness_SpawnAsync_throws_when_configured()
    {
        var harness = new TestHarness();
        harness.Configure(b => b.WithSpawnFailure());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.SpawnAsync(
                new WeaveFleet.Application.Harnesses.HarnessSpawnOptions
                {
                    SessionId = "sess-1",
                    WorkingDirectory = "/tmp"
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task TestHarness_CheckAvailabilityAsync_always_available()
    {
        var harness = new TestHarness();
        var result = await harness.CheckAvailabilityAsync(CancellationToken.None);

        Assert.True(result.Available);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void TestHarness_Type_is_opencode()
    {
        var harness = new TestHarness();
        Assert.Equal("opencode", harness.Type);
    }
}
