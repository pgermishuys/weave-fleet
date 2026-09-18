using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

public sealed class OpenCode2MapperTests
{
    private const string FleetSession = "fleet-session-1";

    [Fact]
    public async Task A_recorded_text_turn_streams_a_reply_and_goes_idle()
    {
        var events = await MapSessionAsync("text-and-tool-turn.sse", OpenCode2Fixtures.TextSession, turns: 1);

        var message = OpenCode2Fixtures.TextTurnMessage;
        var textPart = OpenCode2Mapper.PartId(message, "text", 0);
        events.Select(Describe).ShouldBe(
        [
            "session.status busy",
            $"message.updated {message} open",
            $"message.part.updated {message}-step-start step-start",
            $"message.part.updated {textPart} text ''",
            $"message.part.delta {textPart} 'Hello from the '",
            $"message.part.delta {textPart} 'fake model.'",
            $"message.part.updated {textPart} text 'Hello from the fake model.'",
            $"message.updated {message} completed",
            $"message.part.updated {message}-step-finish step-finish stop",
            "session.idle",
        ]);
    }

    [Fact]
    public async Task The_assistant_message_says_which_agent_and_model_answered_and_what_it_used()
    {
        var events = await MapSessionAsync("text-and-tool-turn.sse", OpenCode2Fixtures.TextSession, turns: 1);

        var completed = events.Last(e => e.Type == EventTypes.MessageUpdated).Payload!.Value.GetProperty("info");
        completed.GetProperty("role").GetString().ShouldBe("assistant");
        completed.GetProperty("sessionID").GetString().ShouldBe(FleetSession);
        completed.GetProperty("agent").GetString().ShouldBe("build");
        completed.GetProperty("modelID").GetString().ShouldBe("fake-model");
        completed.GetProperty("providerID").GetString().ShouldBe("fakellm");
        completed.GetProperty("finish").GetString().ShouldBe("stop");
        completed.GetProperty("tokens").GetProperty("input").GetDouble().ShouldBe(10);
        completed.GetProperty("tokens").GetProperty("output").GetDouble().ShouldBe(5);
        completed.GetProperty("time").TryGetProperty("completed", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Every_mapped_event_translates_into_the_domain_events_the_client_renders()
    {
        var events = await MapSessionAsync("text-and-tool-turn.sse", OpenCode2Fixtures.TextSession, turns: 1);
        var translator = new DomainEventTranslator(NullLogger<DomainEventTranslator>.Instance);

        var domainEvents = events
            .Select(evt => translator.Translate(evt with { FleetSessionId = FleetSession }))
            .ToList();

        events.Where((evt, i) => domainEvents[i] is null).Select(evt => evt.Type + " " + evt.Payload!.Value.GetRawText()).ShouldBeEmpty();
        domainEvents[0].ShouldBeOfType<TurnStarted>();
        domainEvents.OfType<MessagePartDeltaStreamed>().Select(d => d.Payload.Delta).ShouldBe(["Hello from the ", "fake model."]);
        domainEvents.OfType<MessagePartUpdated>().Select(p => p.Payload.Part).OfType<TextMessageEventPart>().Last()
            .Text.ShouldBe("Hello from the fake model.");
        domainEvents.OfType<MessagePartUpdated>().Select(p => p.Payload.Part).OfType<StepFinishedMessageEventPart>().Single()
            .Tokens!.Output.ShouldBe(5);
        domainEvents[^1].ShouldBeOfType<SessionIdled>();
    }

    [Fact]
    public async Task A_tool_turn_maps_its_text_and_leaves_the_tool_events_out()
    {
        var events = await MapSessionAsync("text-and-tool-turn.sse", OpenCode2Fixtures.TextSession, turns: 2);
        var secondTurn = events.SkipWhile(e => e.Type != EventTypes.SessionIdle).Skip(1).ToList();

        secondTurn[0].Type.ShouldBe(EventTypes.SessionStatus);
        secondTurn[^1].Type.ShouldBe(EventTypes.SessionIdle);
        secondTurn.Where(e => e.Type == EventTypes.MessagePartUpdated).Select(PartType)
            .ShouldBe(["step-start", "step-finish", "step-start", "text", "text", "step-finish"]);
        secondTurn.Where(IsStepFinish).Select(Reason).ShouldBe(["tool-calls", "stop"]);
    }

    [Fact]
    public async Task Step_indexes_count_up_across_the_session()
    {
        var events = await MapSessionAsync("text-and-tool-turn.sse", OpenCode2Fixtures.TextSession, turns: 2);

        events.Where(e => PartType(e) == "step-start")
            .Select(e => e.Payload!.Value.GetProperty("part").GetProperty("index").GetInt32())
            .ShouldBe([0, 1, 2]);
    }

    [Fact]
    public async Task An_interrupted_turn_ends_idle_without_a_failure()
    {
        var events = await MapSessionAsync("permission-form-subagent-interrupt.sse", OpenCode2Fixtures.InterruptedSession);

        events[0].Type.ShouldBe(EventTypes.SessionStatus);
        events[^1].Type.ShouldBe(EventTypes.SessionIdle);
        events.ShouldNotContain(e => e.Type == EventTypes.SessionError);
        events.Where(IsStepFinish).Select(Reason).ShouldBe([null]);
    }

    [Theory]
    [InlineData(OpenCode2Fixtures.PluginToolSession)]
    [InlineData(OpenCode2Fixtures.PermissionSession)]
    [InlineData(OpenCode2Fixtures.FormSession)]
    [InlineData(OpenCode2Fixtures.SubagentSession)]
    public async Task Tools_permissions_forms_and_subagents_do_not_break_a_turn(string session)
    {
        var events = await MapSessionAsync("permission-form-subagent-interrupt.sse", session);

        events[0].Type.ShouldBe(EventTypes.SessionStatus);
        events.Count(e => e.Type == EventTypes.SessionIdle).ShouldBe(1);
        events[^1].Type.ShouldBe(EventTypes.SessionIdle);
    }

    [Fact]
    public void A_failed_turn_says_why_then_goes_idle()
    {
        var mapper = new OpenCode2Mapper(FleetSession);
        var translator = new DomainEventTranslator(NullLogger<DomainEventTranslator>.Instance);
        translator.Translate(mapper.Map(Event("session.execution.started", """{"sessionID":"ses_1"}"""))[0]);

        var events = mapper.Map(Event("session.execution.failed",
            """{"sessionID":"ses_1","error":{"type":"provider","message":"The model is overloaded.","status":529}}"""));

        events.Select(e => e.Type).ShouldBe([EventTypes.SessionError, EventTypes.SessionIdle]);
        var failed = translator.Translate(events[0] with { FleetSessionId = FleetSession }).ShouldBeOfType<TurnFailed>();
        failed.Payload.Error.Name.ShouldBe("provider");
        failed.Payload.Error.Message.ShouldBe("The model is overloaded.");
    }

    [Fact]
    public void A_scheduled_retry_shows_as_retrying_with_the_attempt_and_the_reason()
    {
        var at = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds();
        var events = new OpenCode2Mapper(FleetSession).Map(Event("session.retry.scheduled",
            $$$"""{"sessionID":"ses_1","assistantMessageID":"msg_1","attempt":2,"at":{{{at}}},"error":{"type":"rate_limit","message":"Too many requests"}}"""));

        var status = events.ShouldHaveSingleItem().Payload!.Value.GetProperty("status");
        status.GetProperty("type").GetString().ShouldBe(ActivityStatuses.Retry);
        status.GetProperty("count").GetInt32().ShouldBe(2);
        status.GetProperty("reason").GetString().ShouldBe("Too many requests");
        status.GetProperty("delay").GetInt64().ShouldBeInRange(25_000, 30_000);
    }

    [Fact]
    public void Reasoning_streams_as_a_reasoning_part()
    {
        var mapper = new OpenCode2Mapper(FleetSession);
        var part = OpenCode2Mapper.PartId("msg_1", "reasoning", 1);

        var started = mapper.Map(Event("session.reasoning.started", """{"sessionID":"ses_1","assistantMessageID":"msg_1","ordinal":1}"""));
        var delta = mapper.Map(Event("session.reasoning.delta", """{"sessionID":"ses_1","assistantMessageID":"msg_1","ordinal":1,"delta":"Thinking"}"""));
        var ended = mapper.Map(Event("session.reasoning.ended", """{"sessionID":"ses_1","assistantMessageID":"msg_1","ordinal":1,"text":"Thinking it over"}"""));

        Describe(started.ShouldHaveSingleItem()).ShouldBe($"message.part.updated {part} reasoning ''");
        Describe(delta.ShouldHaveSingleItem()).ShouldBe($"message.part.delta {part} 'Thinking'");
        Describe(ended.ShouldHaveSingleItem()).ShouldBe($"message.part.updated {part} reasoning 'Thinking it over'");
    }

    [Theory]
    [InlineData("session.something.new", """{"sessionID":"ses_1"}""")]
    [InlineData("session.text.delta", """{"sessionID":"ses_1"}""")]
    [InlineData("session.step.started", """{"sessionID":"ses_1","model":"not an object"}""")]
    [InlineData("session.execution.started", "\"not an object\"")]
    public void Unknown_or_incomplete_events_map_to_nothing(string type, string data)
    {
        new OpenCode2Mapper(FleetSession).Map(Event(type, data)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_step_reports_its_own_tokens_and_model_to_analytics()
    {
        var mapper = new OpenCode2Mapper(FleetSession);
        var usage = new List<WeaveFleet.Application.Analytics.TokenEventData>();
        foreach (var evt in (await OpenCode2Fixtures.ReadEventsAsync("text-and-tool-turn.sse")).Where(e => e.SessionId == OpenCode2Fixtures.TextSession))
        {
            if (mapper.TryReadStepUsage(evt, "project-1", "Project", "/work/spike/proj", "local-user") is { } step)
                usage.Add(step);
            mapper.Map(evt);
        }

        // Three steps (one text turn, then a tool call and its answer); session.usage.updated isn't counted again.
        usage.Count.ShouldBe(3);
        usage.ShouldAllBe(u => u.ModelId == "fake-model" && u.ProviderId == "fakellm" && u.SessionId == FleetSession);
        usage.Sum(u => u.TokensInput).ShouldBe(30);
        usage.Select(u => u.EventId).Distinct().Count().ShouldBe(3);
    }

    private static async Task<List<HarnessEvent>> MapSessionAsync(string fixture, string session, int? turns = null)
    {
        var mapper = new OpenCode2Mapper(FleetSession);
        var mapped = new List<HarnessEvent>();
        var idles = 0;
        foreach (var evt in await OpenCode2Fixtures.ReadEventsAsync(fixture))
        {
            if (evt.SessionId != session)
                continue;

            foreach (var harnessEvent in mapper.Map(evt))
            {
                mapped.Add(harnessEvent);
                if (harnessEvent.Type == EventTypes.SessionIdle && ++idles == turns)
                    return mapped;
            }
        }

        mapped.ShouldNotBeEmpty();
        return mapped;
    }

    private static OpenCode2Event Event(string type, string data) => new()
    {
        Id = "evt_1",
        Created = 1_789_763_753_000,
        Type = type,
        Data = JsonDocument.Parse(data).RootElement.Clone(),
    };

    private static string Describe(HarnessEvent evt)
    {
        var payload = evt.Payload!.Value;
        return evt.Type switch
        {
            EventTypes.SessionStatus => $"{evt.Type} {payload.GetProperty("status").GetProperty("type").GetString()}",
            EventTypes.MessageUpdated => $"{evt.Type} {payload.GetProperty("info").GetProperty("id").GetString()} "
                + (payload.GetProperty("info").GetProperty("time").TryGetProperty("completed", out _) ? "completed" : "open"),
            EventTypes.MessagePartUpdated => DescribePart(payload.GetProperty("part")),
            EventTypes.MessagePartDelta => $"{evt.Type} {payload.GetProperty("partID").GetString()} '{payload.GetProperty("delta").GetString()}'",
            _ => evt.Type,
        };
    }

    private static string DescribePart(JsonElement part)
    {
        var type = part.GetProperty("type").GetString();
        var text = part.TryGetProperty("text", out var t) ? $" '{t.GetString()}'" : "";
        var reason = part.TryGetProperty("reason", out var r) ? $" {r.GetString()}" : "";
        return $"message.part.updated {part.GetProperty("id").GetString()} {type}{text}{reason}";
    }

    private static string? PartType(HarnessEvent evt)
        => evt.Type == EventTypes.MessagePartUpdated ? evt.Payload!.Value.GetProperty("part").GetProperty("type").GetString() : null;

    private static bool IsStepFinish(HarnessEvent evt) => PartType(evt) == "step-finish";

    private static string? Reason(HarnessEvent evt)
        => evt.Payload!.Value.GetProperty("part").TryGetProperty("reason", out var reason) ? reason.GetString() : null;
}
