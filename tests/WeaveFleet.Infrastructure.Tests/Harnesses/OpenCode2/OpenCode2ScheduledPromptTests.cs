using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// A prompt a schedule sends (OpenCode 2.2.1, <c>experimental.schedules</c>): a <c>user</c> item in V2's inbox under an
/// id of V2's own (<c>msg_sch_…_N</c>), with <c>metadata.schedule</c>. Fleet never sent it, so nothing of Fleet's shows it.
/// </summary>
public sealed class OpenCode2ScheduledPromptTests
{
    private const string FleetSession = "fleet-session-1";
    private const string Fire = "msg_sch_18662af63001G2JCjtpTAOtXq4_1";

    [Fact]
    public void A_scheduled_prompt_shows_as_a_message_of_the_sessions_own()
    {
        var mapped = new OpenCode2Mapper(FleetSession).Map(Event("""
            {"inboxID":"FIRE","sessionID":"ses_1","item":{"type":"user","payload":{"text":"Look at the PR again.","metadata":{"schedule":{"id":"sch_1","name":"look again","run":1,"dueAt":1793255918503,"late":false}}},"delivery":"steer"}}
            """.Replace("FIRE", Fire))).Select(Translate).ToList();

        var message = mapped.OfType<MessageUpdated>().ShouldHaveSingleItem().Payload.Info;
        message.Id.ShouldBe(Fire);
        // Not "user": the relay drops a harness's user messages as echoes of Fleet's own.
        message.Role.ShouldBe(OpenCode2Mapper.NoticeRole);
        mapped.OfType<MessagePartUpdated>().Select(p => p.Payload.Part).OfType<TextMessageEventPart>()
            .ShouldHaveSingleItem().Text.ShouldBe("Look at the PR again.");
    }

    [Fact]
    public void A_prompt_the_user_sent_is_still_left_to_Fleets_own_message()
    {
        new OpenCode2Mapper(FleetSession).Map(Event("""
            {"inboxID":"msg_1","sessionID":"ses_1","item":{"type":"user","payload":{"text":"say hello"},"delivery":"steer"}}
            """)).ShouldBeEmpty();
    }

    private static DomainEvent? Translate(HarnessEvent evt)
        => new DomainEventTranslator(NullLogger<DomainEventTranslator>.Instance).Translate(evt with { FleetSessionId = FleetSession });

    private static OpenCode2Event Event(string data) => new()
    {
        Id = "evt_1",
        Created = 1_793_255_918_503,
        Type = "session.inbox.enqueued",
        Data = JsonDocument.Parse(data).RootElement.Clone(),
    };
}
