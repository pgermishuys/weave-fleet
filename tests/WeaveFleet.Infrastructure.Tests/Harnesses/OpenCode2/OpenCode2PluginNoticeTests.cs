using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// A note a plugin writes into the session for the user: a <c>synthetic</c> message with a <c>description</c>, the line
/// V2's own transcript shows. Seen live on V2 2.0.27 with Weave 0.2.0-next.5: <c>/weave:start</c> with no plan answers
/// this way and starts nothing, and the note waits in the inbox (<c>resume: false</c>) until the next prompt.
/// </summary>
public sealed class OpenCode2PluginNoticeTests
{
    private const string FleetSession = "fleet-session-1";
    private const string Session = "ses_ec1c84712ffeDgIkZ60zS733Lj";
    private const string Note = "msg_13e383ced001iT914DvxGhh7af";
    private const string Description = "Choose one plan: /weave:start <plan-name>";
    private const string Text = "Choose one plan: /weave:start <plan-name>\nAvailable: demo-plan";

    [Fact]
    public void A_plugins_note_shows_as_a_message_of_the_sessions_own()
    {
        var mapped = new OpenCode2Mapper(FleetSession).Map(Event("session.inbox.enqueued", $$"""
            {"inboxID":"{{Note}}","sessionID":"{{Session}}","item":{"type":"synthetic","payload":{"text":{{JsonSerializer.Serialize(Text)}},"description":"{{Description}}"},"delivery":"steer"} }
            """)).Select(Translate).ToList();

        var message = mapped.OfType<MessageUpdated>().ShouldHaveSingleItem().Payload.Info;
        message.Id.ShouldBe(Note);
        message.Role.ShouldBe(OpenCode2Mapper.NoticeRole);
        // The line V2 shows; the rest is for the model.
        mapped.OfType<MessagePartUpdated>().Select(p => p.Payload.Part).OfType<TextMessageEventPart>()
            .ShouldHaveSingleItem().Text.ShouldBe(Description);
    }

    [Fact]
    public void A_plugins_note_caught_up_from_history_reads_the_same()
    {
        var mapped = new OpenCode2Mapper(FleetSession).MapMessage(SyntheticMessage(Description)).Select(Translate).ToList();

        mapped.OfType<MessageUpdated>().ShouldHaveSingleItem().Payload.Info.Role.ShouldBe(OpenCode2Mapper.NoticeRole);
        mapped.OfType<MessagePartUpdated>().Select(p => p.Payload.Part).OfType<TextMessageEventPart>()
            .ShouldHaveSingleItem().Text.ShouldBe(Description);
    }

    [Fact]
    public void A_plugins_note_read_back_from_history_shows_and_one_without_a_description_does_not()
    {
        var history = OpenCode2History.ToHarnessMessages([SyntheticMessage(Description), SyntheticMessage(description: null) with { Id = "msg_reminder" }]);

        var note = history.ShouldHaveSingleItem();
        note.Id.ShouldBe(Note);
        note.Role.ShouldBe(OpenCode2Mapper.NoticeRole);
        note.Parts.OfType<TextPart>().ShouldHaveSingleItem().Text.ShouldBe(Description);
    }

    [Fact]
    public void A_plugins_note_still_in_the_inbox_shows_on_a_reload()
    {
        // It waits there until the next prompt takes it in.
        var pending = OpenCode2History.PendingPrompts(
        [
            new OpenCode2InboxItem
            {
                Id = Note,
                Type = "synthetic",
                Time = new OpenCode2MessageTimes { Created = 1_792_045_235_438 },
                Payload = new OpenCode2InboxPayload { Text = Text, Description = Description },
            },
            new OpenCode2InboxItem
            {
                Id = "msg_reminder",
                Type = "synthetic",
                Payload = new OpenCode2InboxPayload { Text = "You are in Plan mode." },
            },
        ]);

        var note = pending.ShouldHaveSingleItem();
        note.Id.ShouldBe(Note);
        note.Role.ShouldBe(OpenCode2Mapper.NoticeRole);
        note.Parts.OfType<TextPart>().ShouldHaveSingleItem().Text.ShouldBe(Description);
    }

    [Fact]
    public void A_background_completion_with_a_description_still_shows_its_whole_notice()
    {
        // V2 describes these too; the client reads the tag in the text to finish the call's card.
        var metadata = JsonDocument.Parse("""{"source":"subagent","childID":"ses_child","state":"completed"}""").RootElement.Clone();
        var history = OpenCode2History.ToHarnessMessages(
        [
            SyntheticMessage("Slow helper") with { Text = """<subagent sessionID="ses_child" state="completed">done</subagent>""", Metadata = metadata },
        ]);

        history.ShouldHaveSingleItem().Parts.OfType<TextPart>().ShouldHaveSingleItem().Text.ShouldStartWith("<subagent ");
    }

    private static OpenCode2Message SyntheticMessage(string? description) => new()
    {
        Id = Note,
        Type = "synthetic",
        Time = new OpenCode2MessageTimes { Created = 1_792_045_235_438 },
        Text = Text,
        Description = description,
    };

    private static DomainEvent? Translate(HarnessEvent evt)
        => new DomainEventTranslator(NullLogger<DomainEventTranslator>.Instance).Translate(evt with { FleetSessionId = FleetSession });

    private static OpenCode2Event Event(string type, string data) => new()
    {
        Id = "evt_13e383cee00140oZDOd9Kv3qZZ",
        Created = 1_792_045_235_438,
        Type = type,
        Data = JsonDocument.Parse(data).RootElement.Clone(),
    };
}
