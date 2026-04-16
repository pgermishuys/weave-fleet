using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.TestHarness.Tests;

public sealed class TestScenarioBuilderTests
{
    [Fact]
    public void Default_build_produces_empty_scenario()
    {
        var scenario = new TestScenarioBuilder().Build();

        scenario.Messages.ShouldBeEmpty();
        scenario.PromptResponses.ShouldBeEmpty();
        scenario.ThrowOnSpawn.ShouldBeFalse();
        scenario.ThrowOnSendPrompt.ShouldBeFalse();
        scenario.InitialStatus.ShouldBe(HarnessSessionStatus.Idle);
    }

    [Fact]
    public void WithUserMessage_adds_user_message()
    {
        var scenario = new TestScenarioBuilder()
            .WithUserMessage("msg-1", "Hello world")
            .Build();

        scenario.Messages.Count.ShouldBe(1);
        var msg = scenario.Messages[0];
        msg.Id.ShouldBe("msg-1");
        msg.Role.ShouldBe("user");
        msg.TextContent.ShouldBe("Hello world");
        var part = msg.Parts[0].ShouldBeOfType<TextPart>();
        part.Text.ShouldBe("Hello world");
    }

    [Fact]
    public void WithAssistantMessage_adds_assistant_message()
    {
        var scenario = new TestScenarioBuilder()
            .WithAssistantMessage("msg-2", "Response text")
            .Build();

        scenario.Messages.Count.ShouldBe(1);
        var msg = scenario.Messages[0];
        msg.Role.ShouldBe("assistant");
        msg.TextContent.ShouldBe("Response text");
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

        scenario.Messages.Count.ShouldBe(1);
        var msg = scenario.Messages[0];
        msg.Parts.Count.ShouldBe(2);
        msg.Parts[0].ShouldBeOfType<TextPart>();
        msg.Parts[1].ShouldBeOfType<ToolUsePart>();
    }

    [Fact]
    public void Multiple_messages_preserved_in_order()
    {
        var scenario = new TestScenarioBuilder()
            .WithUserMessage("u1", "First")
            .WithAssistantMessage("a1", "Second")
            .WithUserMessage("u2", "Third")
            .Build();

        scenario.Messages.Count.ShouldBe(3);
        scenario.Messages[0].Id.ShouldBe("u1");
        scenario.Messages[1].Id.ShouldBe("a1");
        scenario.Messages[2].Id.ShouldBe("u2");
    }

    [Fact]
    public void WithSimpleTextResponse_enqueues_6_events()
    {
        var scenario = new TestScenarioBuilder()
            .WithSimpleTextResponse("sess-1", "msg-1", "Hello back!")
            .Build();

        scenario.PromptResponses.Count.ShouldBe(1);
        var events = scenario.PromptResponses.Dequeue();
        events.Count.ShouldBe(6);
        events[0].Event.Type.ShouldBe("session.status");
        events[1].Event.Type.ShouldBe("message.updated");     // user message
        events[2].Event.Type.ShouldBe("message.part.updated"); // user part
        events[3].Event.Type.ShouldBe("message.updated");     // assistant message
        events[4].Event.Type.ShouldBe("message.part.updated"); // assistant part
        events[5].Event.Type.ShouldBe("session.idle");
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

        scenario.PromptResponses.Count.ShouldBe(1);
        var events = scenario.PromptResponses.Dequeue();
        events.Count.ShouldBe(1);
        events[0].Event.Type.ShouldBe("custom.event");
    }

    [Fact]
    public void Multiple_prompt_responses_enqueued_in_order()
    {
        var scenario = new TestScenarioBuilder()
            .WithSimpleTextResponse("sess-1", "msg-1", "First")
            .WithSimpleTextResponse("sess-1", "msg-2", "Second")
            .Build();

        scenario.PromptResponses.Count.ShouldBe(2);
        var first = scenario.PromptResponses.Dequeue();
        first[0].Event.Type.ShouldBe("session.status");
        var second = scenario.PromptResponses.Dequeue();
        second[0].Event.Type.ShouldBe("session.status");
    }

    [Fact]
    public void WithSpawnFailure_sets_ThrowOnSpawn()
    {
        var scenario = new TestScenarioBuilder()
            .WithSpawnFailure()
            .Build();

        scenario.ThrowOnSpawn.ShouldBeTrue();
        scenario.ThrowOnSendPrompt.ShouldBeFalse();
    }

    [Fact]
    public void WithSendPromptFailure_sets_ThrowOnSendPrompt()
    {
        var scenario = new TestScenarioBuilder()
            .WithSendPromptFailure()
            .Build();

        scenario.ThrowOnSpawn.ShouldBeFalse();
        scenario.ThrowOnSendPrompt.ShouldBeTrue();
    }

    [Fact]
    public void WithInitialStatus_sets_status()
    {
        var scenario = new TestScenarioBuilder()
            .WithInitialStatus(HarnessSessionStatus.Running)
            .Build();

        scenario.InitialStatus.ShouldBe(HarnessSessionStatus.Running);
    }

    // ── TestHarnessRuntime integration ──────────────────────────────────────────

    [Fact]
    public async Task TestHarness_Configure_fluent_sets_scenario()
    {
        var harness = new TestHarnessRuntime();
        harness.Configure(b => b.WithUserMessage("msg-1", "Test message"));

        var instance = (TestHarnessSession)await harness.SpawnAsync(
            new WeaveFleet.Application.Harnesses.HarnessSpawnOptions
            {
                SessionId = "sess-1",
                WorkingDirectory = "/tmp",
                OwnerUserId = "test-user"
            },
            CancellationToken.None);

        var page = await instance.GetMessagesAsync(null, CancellationToken.None);
        page.Messages.Count.ShouldBe(1);
        page.Messages[0].Id.ShouldBe("msg-1");
    }

    [Fact]
    public async Task TestHarness_SpawnAsync_throws_when_configured()
    {
        var harness = new TestHarnessRuntime();
        harness.Configure(b => b.WithSpawnFailure());

        await Should.ThrowAsync<InvalidOperationException>(() =>
            harness.SpawnAsync(
                new WeaveFleet.Application.Harnesses.HarnessSpawnOptions
                {
                    SessionId = "sess-1",
                    WorkingDirectory = "/tmp",
                    OwnerUserId = "test-user"
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task TestHarness_CheckAvailabilityAsync_always_available()
    {
        var harness = new TestHarnessRuntime();
        var result = await harness.CheckAvailabilityAsync(CancellationToken.None);

        result.Available.ShouldBeTrue();
        result.Reason.ShouldBeNull();
    }

    [Fact]
    public void TestHarness_Type_is_opencode()
    {
        var harness = new TestHarness();
        harness.Type.ShouldBe("opencode");
    }
}

