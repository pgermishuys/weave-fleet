using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// A reopened OpenCode 2 session reads V2's history (<c>tools-questions.messages.json</c>) through the message proxy,
/// and has to show what the live stream showed (<c>tools-questions.sse</c>, the same session).
/// </summary>
public sealed class OpenCode2HistoryTests
{
    private const string FleetSession = "fleet-session-1";

    [Fact]
    public async Task A_reopened_session_shows_every_part_as_the_live_stream_left_it()
    {
        var live = await LivePartsAsync();
        var snapshot = await ReopenAsync(History());

        var reopened = snapshot.Messages
            .SelectMany(m => m.Parts)
            .Where(p => p is not StepFinishedMessageEventPart)
            .ToDictionary(p => p.Id, Serialize);

        // Everything the conversation renders: every text and tool part, with the same id and the same content.
        live.Keys.ShouldNotBeEmpty();
        reopened.Keys.Where(id => !id.StartsWith("msg_fleetprompt", StringComparison.Ordinal)).OrderBy(id => id, StringComparer.Ordinal)
            .ShouldBe(live.Keys.OrderBy(id => id, StringComparer.Ordinal));
        foreach (var (id, json) in live)
            reopened[id].ShouldBe(json, $"part {id}");
    }

    [Fact]
    public async Task A_reopened_session_shows_the_prompts_under_the_ids_fleet_sent_them_with()
    {
        var snapshot = await ReopenAsync(History());

        var prompts = snapshot.Messages.Where(m => m.Info.Role == "user").ToList();
        prompts.Select(m => m.Info.Id).ShouldBe(
        [
            "msg_fleetprompt0001aaaaaaaaaa",
            "msg_fleetprompt0002aaaaaaaaaa",
            "msg_fleetprompt0003aaaaaaaaaa",
            "msg_fleetprompt0004aaaaaaaaaa",
        ]);
        // The id Fleet gives a prompt's text when it shows the prompt it sent.
        var text = prompts[0].Parts.ShouldHaveSingleItem().ShouldBeOfType<TextMessageEventPart>();
        text.Id.ShouldBe("msg_fleetprompt0001aaaaaaaaaa-text-0");
        text.Text.ShouldBe("please use a tool");
    }

    [Fact]
    public void History_is_oldest_first_and_leaves_out_what_the_conversation_does_not_show()
    {
        var messages = History();

        messages.Select(m => m.Role).ShouldBe(
        [
            "user", "assistant", "assistant",
            "user", "assistant", "assistant",
            "user", "assistant", "assistant",
            "user", "assistant",
        ]);
        messages.Where(m => m.Role == "assistant").ShouldAllBe(m => m.Agent == "build" && m.ModelId == "fake-model");
    }

    [Fact]
    public void A_finished_step_reports_what_it_used_and_why_it_stopped()
    {
        var shell = History().Single(m => m.Id == OpenCode2Fixtures.ShellMessage);

        shell.Finish.ShouldBe("tool-calls");
        var step = shell.Parts.OfType<StepFinishPart>().ShouldHaveSingleItem();
        step.Reason.ShouldBe("tool-calls");
        step.TokensInput.ShouldBe(10);
        step.TokensOutput.ShouldBe(5);
        step.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public void An_interrupted_step_is_not_shown_as_a_failure()
    {
        // The dismissed question's step ended "aborted: Step interrupted"; live, an interrupt shows no failure card.
        History().ShouldAllBe(m => m.Error == null);
    }

    [Fact]
    public void A_step_that_failed_says_why()
    {
        var messages = OpenCode2History.ToHarnessMessages(
        [
            new OpenCode2Message
            {
                Id = "msg_1",
                Type = "assistant",
                Time = new OpenCode2MessageTimes { Created = 1, Completed = 2 },
                Content = [],
                Finish = "error",
                Error = new OpenCode2StructuredError { Type = "provider", Message = "The model is overloaded." },
            },
        ]);

        var error = messages.ShouldHaveSingleItem().Error.ShouldNotBeNull();
        error.Name.ShouldBe("provider");
        error.Message.ShouldBe("The model is overloaded.");
    }

    [Fact]
    public void Text_and_reasoning_are_numbered_separately_as_V2_streams_them()
    {
        var message = OpenCode2History.ToHarnessMessages(
        [
            new OpenCode2Message
            {
                Id = "msg_1",
                Type = "assistant",
                Time = new OpenCode2MessageTimes { Created = 1 },
                Content =
                [
                    new OpenCode2Content { Type = "reasoning", Text = "Thinking" },
                    new OpenCode2Content { Type = "text", Text = "First" },
                    new OpenCode2Content { Type = "reasoning", Text = "More" },
                    new OpenCode2Content { Type = "text", Text = "Second" },
                ],
            },
        ]).ShouldHaveSingleItem();

        message.Parts.Select(p => p switch
        {
            TextPart t => t.PartId,
            ReasoningPart r => r.PartId,
            _ => p.GetType().Name,
        }).ShouldBe(["msg_1-reasoning-0", "msg_1-text-0", "msg_1-reasoning-1", "msg_1-text-1"]);
    }

    [Fact]
    public void A_tool_still_streaming_its_input_is_pending_without_arguments()
    {
        using var state = JsonDocument.Parse("""{"status":"streaming","input":"{\"comm"}""");
        var message = OpenCode2History.ToHarnessMessages(
        [
            new OpenCode2Message
            {
                Id = "msg_1",
                Type = "assistant",
                Time = new OpenCode2MessageTimes { Created = 1 },
                Content = [new OpenCode2Content { Type = "tool", Id = "call_1", Name = "shell", State = state.RootElement.Deserialize(OpenCode2JsonContext.Default.OpenCode2ToolState) }],
            },
        ]).ShouldHaveSingleItem();

        var tool = message.Parts.OfType<ToolUsePart>().ShouldHaveSingleItem();
        tool.State.ShouldBe(ToolUseState.Pending);
        tool.Arguments.ValueKind.ShouldBe(JsonValueKind.Undefined);
        message.Parts.OfType<StepFinishPart>().ShouldBeEmpty();
    }

    /// <summary>The live stream's last word on every part, as the client ends up with it.</summary>
    private static async Task<Dictionary<string, string>> LivePartsAsync()
    {
        var mapper = new OpenCode2Mapper(FleetSession);
        var translator = new DomainEventTranslator(NullLogger<DomainEventTranslator>.Instance);
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var evt in await OpenCode2Fixtures.ReadEventsAsync("tools-questions.sse"))
        {
            if (evt.SessionId != OpenCode2Fixtures.ToolsSession)
                continue;

            foreach (var harnessEvent in mapper.Map(evt))
            {
                if (translator.Translate(harnessEvent with { FleetSessionId = FleetSession }) is MessagePartUpdated { Payload.Part: var part }
                    && part is not (StepStartedMessageEventPart or StepFinishedMessageEventPart))
                {
                    parts[part.Id] = Serialize(part);
                }
            }
        }

        return parts;
    }

    /// <summary>The history the way <see cref="OpenCode2HarnessSession.GetMessagesAsync"/> hands it over: oldest first.</summary>
    private static IReadOnlyList<HarnessMessage> History()
    {
        var page = JsonSerializer.Deserialize(OpenCode2Fixtures.Read("tools-questions.messages.json"), OpenCode2JsonContext.Default.OpenCode2MessagePage)!;
        return OpenCode2History.ToHarnessMessages(page.Data!.Reverse());
    }

    private static async Task<SessionSnapshot> ReopenAsync(IReadOnlyList<HarnessMessage> messages)
    {
        var sessions = new InMemorySessionRepository();
        sessions.Seed(new Session
        {
            Id = FleetSession,
            InstanceId = "inst-1",
            HarnessType = "opencode2",
            Title = "Session",
            Status = "active",
            UserId = "user-1",
        });
        var instances = new InstanceTracker();
        instances.Register("inst-1", new FakeHarnessSession("inst-1")
        {
            GetMessagesBehavior = (_, _) => Task.FromResult(new MessagePage(messages, false)),
        });

        var proxy = new OpenCodeSessionMessageProxy(
            sessions,
            instances,
            new SessionActivityTracker(),
            new InMemoryDelegationRepository(),
            new FakeSessionSnapshotBuilder(),
            new ServiceCollection().BuildServiceProvider(),
            new HarnessRegistry([new OpenCode2Harness()], []),
            NullLogger<OpenCodeSessionMessageProxy>.Instance);

        var snapshot = await proxy.GetSnapshotAsync(FleetSession);
        snapshot.IsPartial.ShouldBeFalse();
        return snapshot;
    }

    private static string Serialize(MessageEventPart part)
        => JsonSerializer.Serialize(
            new MessagePartUpdatedPayload { SessionId = FleetSession, Part = part },
            InfrastructureJsonContext.Default.MessagePartUpdatedPayload);
}
