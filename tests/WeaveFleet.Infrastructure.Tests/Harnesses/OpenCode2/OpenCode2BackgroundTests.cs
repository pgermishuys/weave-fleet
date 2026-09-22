using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// Work OpenCode 2 moved into the background, from a recorded session (<c>background-tasks.sse</c>): a shell and a
/// subagent, each called with <c>background: true</c>. Both calls succeed at once while their work goes on, and V2
/// posts a notice into the session when the work really finishes.
/// </summary>
public sealed class OpenCode2BackgroundTests
{
    private const string FleetSession = "fleet-session-1";
    private const string Session = "ses_f35d772d0ffe5Nudf5Dt2Eb6ty";
    private const string Child = "ses_f35d720beffeFpWwyVed1mYSiQ";
    private const string ShellMessage = "msg_0ca289110001bhgQog6jeJoWsh";
    private const string ShellCall = "call_1790098051372";
    private const string SubagentMessage = "msg_0ca28df1a001RDF2i2evUXskQB";
    private const string SubagentCall = "call_1790098071345";
    private const string ShellNotice = "msg_0ca28919a0013m0p8sWWitFbtq";
    private const string SubagentNotice = "msg_0ca28df4a001HNL2xan12vSk2K";

    [Fact]
    public async Task A_backgrounded_shell_keeps_its_card_running_and_says_where_its_output_goes()
    {
        var card = (await ToolPartsAsync(ShellCall))[^1];

        card.ToolName.ShouldBe("shell");
        var running = card.State.ShouldBeOfType<ToolRunningState>();
        running.Background.ShouldBeTrue();
        running.Input!.Value.GetProperty("background").GetBoolean().ShouldBeTrue();
        running.Output!.Value.GetString().ShouldNotBeNull().ShouldContain("moved to the background");
        running.Metadata!.Value.GetProperty("shellID").GetString().ShouldBe("sh_0ca289192001atCtTyB6Js2f3j");
        running.Metadata!.Value.GetProperty("status").GetString().ShouldBe("running");
    }

    [Fact]
    public async Task A_backgrounded_subagent_keeps_its_card_running_and_carries_its_child_session()
    {
        var running = (await ToolPartsAsync(SubagentCall))[^1].State.ShouldBeOfType<ToolRunningState>();

        running.Background.ShouldBeTrue();
        running.Metadata!.Value.GetProperty("sessionID").GetString().ShouldBe(Child);
        running.Metadata!.Value.GetProperty("status").GetString().ShouldBe("running");
    }

    [Fact]
    public async Task An_ordinary_call_in_the_same_recording_still_completes()
    {
        // The child's own shell call ran in the foreground: its result says completed, not running.
        var mapper = new OpenCode2Mapper(FleetSession);
        var parts = new List<ToolMessageEventPart>();
        foreach (var evt in (await OpenCode2Fixtures.ReadEventsAsync("background-tasks.sse")).Where(e => e.SessionId == Child))
            parts.AddRange(ToolParts(mapper.Map(evt)));

        parts[^1].State.ShouldBeOfType<ToolCompletedState>().Output!.Value.GetString().ShouldNotBeNull().ShouldContain("child-worked");
    }

    [Fact]
    public async Task The_notice_that_the_work_finished_shows_as_a_message_of_the_sessions_own()
    {
        var mapped = await MapAsync();

        // The shell's notice, then the subagent's: the text V2 gave the model, under the id it stored it with.
        var notices = mapped.Select(Translate).OfType<MessageUpdated>().Where(m => m.Payload.Info.Role == OpenCode2Mapper.NoticeRole).ToList();
        notices.Select(m => m.Payload.Info.Id).ShouldBe([ShellNotice, SubagentNotice]);

        var texts = mapped.Select(Translate).OfType<MessagePartUpdated>()
            .Select(p => p.Payload.Part).OfType<TextMessageEventPart>()
            .Where(p => p.MessageId is ShellNotice or SubagentNotice)
            .ToList();
        texts[0].Text.ShouldContain(""""<shell id="sh_0ca289192001atCtTyB6Js2f3j" state="completed" """".TrimEnd());
        texts[0].Text.ShouldContain("shell-finished-late");
        texts[1].Text.ShouldBe($"""
            <subagent sessionID="{Child}" state="completed" description="Slow helper">
            The child finished its slow task.
            </subagent>
            """);
    }

    [Fact]
    public async Task A_prompt_on_the_inbox_is_not_a_notice()
    {
        // Every message the session is given arrives on the inbox, prompts included: only a completion shows.
        var mapped = await MapAsync();

        mapped.Select(Translate).OfType<MessagePartUpdated>()
            .Select(p => p.Payload.Part).OfType<TextMessageEventPart>()
            .ShouldNotContain(p => p.Text.Contains("please run bg-shell", StringComparison.Ordinal));
    }

    [Fact]
    public void Another_synthetic_message_is_not_a_notice()
    {
        // Plan mode's reminders are synthetic too; they're instructions to the model, not news for the user.
        new OpenCode2Mapper(FleetSession).Map(Event("session.inbox.enqueued", $$"""
            {"inboxID":"msg_1","sessionID":"{{Session}}","item":{"type":"synthetic","payload":{"text":"You are in Plan mode."},"delivery":"steer"} }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_background_subagents_delegation_stays_running_until_its_notice_arrives()
    {
        var mapper = new OpenCode2Mapper(FleetSession);
        var delegations = new List<OpenCode2Delegation>();
        foreach (var evt in (await OpenCode2Fixtures.ReadEventsAsync("background-tasks.sse")).Where(e => e.SessionId == Session))
        {
            if (mapper.TryReadDelegation(evt) is { } delegation)
                delegations.Add(delegation);
            mapper.Map(evt);
        }

        // The call returns while the child works ("running"), and the notice that the child finished ends it.
        delegations.ShouldBe(
        [
            new OpenCode2Delegation(SubagentCall, "general", "Slow helper", ChildSessionId: null, "running"),
            new OpenCode2Delegation(SubagentCall, "general", "Slow helper", Child, "running"),
            new OpenCode2Delegation(SubagentCall, "general", "Slow helper", Child, "running"),
            new OpenCode2Delegation(SubagentCall, "general", "Slow helper", Child, "completed"),
        ]);
    }

    [Theory]
    [InlineData("error", "error")]
    [InlineData("cancelled", "cancelled")]
    [InlineData("completed", "completed")]
    public void How_the_child_ended_is_how_the_delegation_ends(string state, string expected)
    {
        var mapper = new OpenCode2Mapper(FleetSession);
        mapper.Map(Event("session.tool.input.started", $$"""{"sessionID":"{{Session}}","assistantMessageID":"msg_1","id":"call_1","name":"subagent"}"""));
        mapper.Map(Event("session.tool.called", $$"""{"sessionID":"{{Session}}","assistantMessageID":"msg_1","id":"call_1","input":{"agent":"general","description":"Slow helper","background":true} }"""));
        var success = Event("session.tool.success", $$"""
            {"sessionID":"{{Session}}","assistantMessageID":"msg_1","id":"call_1","content":[],"metadata":{"sessionID":"{{Child}}","status":"running"} }
            """);
        mapper.TryReadDelegation(success)!.Status.ShouldBe("running");
        mapper.Map(success);

        var notice = Event("session.inbox.enqueued", $$"""
            {"inboxID":"msg_2","sessionID":"{{Session}}","item":{"type":"synthetic","payload":{
              "text":"<subagent sessionID=\"{{Child}}\" state=\"{{state}}\" description=\"Slow helper\">\nover\n</subagent>",
              "description":"Slow helper",
              "metadata":{"source":"subagent","childID":"{{Child}}","agent":"General","state":"{{state}}"} } } }
            """);

        mapper.TryReadDelegation(notice).ShouldBe(new OpenCode2Delegation("call_1", "general", "Slow helper", Child, expected));
        // Only once: a second notice for the same child ends nothing.
        mapper.TryReadDelegation(notice).ShouldBeNull();
    }

    [Fact]
    public void A_notice_for_a_shell_is_not_a_delegation()
    {
        new OpenCode2Mapper(FleetSession).TryReadDelegation(Event("session.inbox.enqueued", $$"""
            {"inboxID":"msg_2","sessionID":"{{Session}}","item":{"type":"synthetic","payload":{
              "text":"<shell id=\"sh_1\" state=\"completed\" command=\"ls\">\nout\n</shell>",
              "metadata":{"source":"shell","shellID":"sh_1","jobID":"sh_1","state":"completed","exit":0} } } }
            """)).ShouldBeNull();
    }

    [Fact]
    public void A_backgrounded_call_read_back_from_history_is_still_running()
    {
        // V2 never updates the stored call: it keeps "completed" with the metadata that says the work went on.
        var calls = History().SelectMany(m => m.Parts).OfType<ToolUsePart>().ToList();

        calls.Select(c => c.ToolCallId).ShouldBe([ShellCall, SubagentCall]);
        calls.ShouldAllBe(c => c.State == ToolUseState.Running && c.Background);
        calls[0].Output!.Value.GetString().ShouldNotBeNull().ShouldContain("moved to the background");
    }

    [Fact]
    public void A_notice_read_back_from_history_shows_as_a_message_and_other_synthetics_dont()
    {
        var history = History();
        var notices = history.Where(m => m.Id is ShellNotice or SubagentNotice).ToList();

        notices.Select(m => m.Id).ShouldBe([ShellNotice, SubagentNotice]);
        notices.ShouldAllBe(m => m.Role == OpenCode2Mapper.NoticeRole);
        notices[1].Parts.OfType<TextPart>().ShouldHaveSingleItem().Text.ShouldContain($$""""<subagent sessionID="{{Child}}" """".TrimEnd());
    }

    /// <summary>The session's history, oldest first, the way a reopened session reads it.</summary>
    private static IReadOnlyList<HarnessMessage> History()
    {
        var page = JsonSerializer.Deserialize(OpenCode2Fixtures.Read("background-tasks.messages.json"), OpenCode2JsonContext.Default.OpenCode2MessagePage)!;
        return OpenCode2History.ToHarnessMessages(page.Data!.Reverse());
    }

    private static async Task<List<HarnessEvent>> MapAsync()
    {
        var mapper = new OpenCode2Mapper(FleetSession);
        var mapped = new List<HarnessEvent>();
        foreach (var evt in await OpenCode2Fixtures.ReadEventsAsync("background-tasks.sse"))
        {
            if (evt.SessionId == Session)
                mapped.AddRange(mapper.Map(evt));
        }

        return mapped;
    }

    private static async Task<List<ToolMessageEventPart>> ToolPartsAsync(string callId)
        => ToolParts(await MapAsync()).Where(p => p.CallId == callId).ToList();

    private static List<ToolMessageEventPart> ToolParts(IEnumerable<HarnessEvent> events)
        => events
            .Where(e => e.Type == EventTypes.MessagePartUpdated)
            .Select(Translate)
            .OfType<MessagePartUpdated>()
            .Select(p => p.Payload.Part)
            .OfType<ToolMessageEventPart>()
            .ToList();

    private static DomainEvent? Translate(HarnessEvent evt)
        => new DomainEventTranslator(NullLogger<DomainEventTranslator>.Instance).Translate(evt with { FleetSessionId = FleetSession });

    private static OpenCode2Event Event(string type, string data) => new()
    {
        Id = "evt_1",
        Created = 1_790_098_051_000,
        Type = type,
        Data = JsonDocument.Parse(data).RootElement.Clone(),
    };
}
