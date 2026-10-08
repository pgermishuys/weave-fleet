using Shouldly;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Tests.Sessions;

public sealed class StreamingRepliesTests
{
    private const string SessionId = "s-1";

    private readonly StreamingReplies _sut = new(TimeProvider.System);

    [Fact]
    public void each_text_delta_goes_out_with_where_it_starts_in_its_part()
    {
        Offset(Delta("msg_a", "prt_1", "Checking the ")).ShouldBe(0);
        Offset(Delta("msg_a", "prt_1", "rate limit ")).ShouldBe(13);
        Offset(Delta("msg_a", "prt_2", "Another part")).ShouldBe(0);
        Offset(Delta("msg_a", "prt_1", "headers")).ShouldBe(24);
    }

    [Fact]
    public void a_part_the_harness_kept_without_its_text_gets_the_text_so_far()
    {
        Stream("msg_a", "prt_1", "Checking the ", "rate limit headers");

        var messages = _sut.Overlay(SessionId, [Message("msg_a", Text("msg_a", "prt_1", ""))]);

        messages.ShouldHaveSingleItem().Parts.ShouldHaveSingleItem()
            .ShouldBeOfType<TextMessageEventPart>().Text.ShouldBe("Checking the rate limit headers");
    }

    [Fact]
    public void a_part_the_harness_has_more_of_keeps_the_harness_text()
    {
        Stream("msg_a", "prt_1", "Checking the ");

        var messages = _sut.Overlay(SessionId, [Message("msg_a", Text("msg_a", "prt_1", "Checking the rate limit headers"))]);

        messages.ShouldHaveSingleItem().Parts.ShouldHaveSingleItem()
            .ShouldBeOfType<TextMessageEventPart>().Text.ShouldBe("Checking the rate limit headers");
    }

    [Fact]
    public void a_reply_the_harness_has_not_kept_is_added_after_the_messages_it_has()
    {
        Stream("msg_b", "prt_1", "Checking the ", "rate limit headers");

        var messages = _sut.Overlay(SessionId, [Message("msg_a", Text("msg_a", "prt_0", "An earlier reply"))]);

        messages.Count.ShouldBe(2);
        var reply = messages[1];
        reply.Info.Id.ShouldBe("msg_b");
        reply.Info.Role.ShouldBe("assistant");
        reply.Info.SessionId.ShouldBe(SessionId);
        reply.Parts.ShouldHaveSingleItem().ShouldBeOfType<TextMessageEventPart>().Text.ShouldBe("Checking the rate limit headers");
    }

    [Fact]
    public void a_part_the_harness_has_not_kept_is_added_to_its_message()
    {
        Stream("msg_a", "prt_2", "Then this");

        var messages = _sut.Overlay(SessionId, [Message("msg_a", Text("msg_a", "prt_1", "First this"))]);

        messages.ShouldHaveSingleItem().Parts.Select(part => ((TextMessageEventPart)part).Text).ShouldBe(["First this", "Then this"]);
    }

    public static TheoryData<DomainEvent> TurnEnds() => new()
    {
        new SessionIdled { Payload = new SessionIdledPayload { SessionId = SessionId } },
        new TurnEnded { Payload = new TurnEndedPayload { SessionId = SessionId, MessageId = "msg_a", Index = 0 } },
        new TurnFailed { Payload = new TurnFailedPayload { SessionId = SessionId, Error = new TurnError { Name = "ProviderError", Message = "Overloaded" } } },
    };

    [Theory]
    [MemberData(nameof(TurnEnds))]
    public void the_text_goes_when_the_turn_ends(DomainEvent end)
    {
        Stream("msg_a", "prt_1", "Half a reply");

        _sut.Observe(SessionId, end);

        _sut.Overlay(SessionId, []).ShouldBeEmpty();
        Offset(Delta("msg_a", "prt_1", "A new turn")).ShouldBe(0);
    }

    [Fact]
    public void the_text_goes_when_the_harness_goes()
    {
        Stream("msg_a", "prt_1", "Half a reply");

        _sut.Forget(SessionId);

        _sut.Overlay(SessionId, []).ShouldBeEmpty();
    }

    [Fact]
    public void another_sessions_replies_stay_out()
    {
        Stream("msg_a", "prt_1", "Not yours");

        _sut.Overlay("s-2", []).ShouldBeEmpty();
    }

    private void Stream(string messageId, string partId, params string[] deltas)
    {
        foreach (var delta in deltas)
            _sut.Observe(SessionId, Delta(messageId, partId, delta));
    }

    private int? Offset(MessagePartDeltaStreamed delta)
        => _sut.Observe(SessionId, delta).ShouldBeOfType<MessagePartDeltaStreamed>().Payload.Offset;

    private static MessagePartDeltaStreamed Delta(string messageId, string partId, string text) => new()
    {
        Payload = new MessagePartDeltaStreamedPayload
        {
            SessionId = SessionId,
            MessageId = messageId,
            PartId = partId,
            Field = "text",
            Delta = text,
        },
    };

    private static MessageLifecyclePayload Message(string messageId, params MessageEventPart[] parts) => new()
    {
        Info = new MessageEventInfo
        {
            Id = messageId,
            Role = "assistant",
            SessionId = SessionId,
            Time = new MessageEventTime { Created = 1 },
        },
        Parts = parts,
    };

    private static TextMessageEventPart Text(string messageId, string partId, string text)
        => new() { Id = partId, SessionId = SessionId, MessageId = messageId, Text = text };
}
