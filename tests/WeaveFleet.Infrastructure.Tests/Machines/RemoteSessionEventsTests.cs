using System.Text.Json;
using WeaveFleet.Domain.Events;
using WeaveFleet.Infrastructure.Machines;

namespace WeaveFleet.Infrastructure.Tests.Machines;

/// <summary>
/// A followed session's events, read back from another machine's hub (<c>{ type, properties }</c>): only what says how a
/// turn answering a message went becomes a domain event again.
/// </summary>
public sealed class RemoteSessionEventsTests
{
    [Fact]
    public void A_reply_names_its_parent()
    {
        var domainEvent = RemoteMachineConnection.ToDomainEvent("s-1", Wire("""
            {"type":"message.updated","properties":{"info":{"id":"reply-1","role":"assistant","sessionID":"s-1","parentID":"msg-1","time":{"created":0}}}}
            """));

        var info = domainEvent.ShouldBeOfType<MessageUpdated>().Payload.Info;
        info.ParentId.ShouldBe("msg-1");
        info.Role.ShouldBe("assistant");
    }

    [Fact]
    public void A_failed_turn_says_why_and_going_idle_names_the_session()
    {
        var failed = RemoteMachineConnection.ToDomainEvent("s-1", Wire("""
            {"type":"turn.failed","properties":{"sessionID":"s-1","error":{"name":"RateLimit","message":"Rate limited."}}}
            """));
        var idle = RemoteMachineConnection.ToDomainEvent("s-1", Wire("""{"type":"session.idled","properties":{}}"""));

        failed.ShouldBeOfType<TurnFailed>().Payload.Error.Message.ShouldBe("Rate limited.");
        idle.ShouldBeOfType<SessionIdled>().Payload.SessionId.ShouldBe("s-1");
    }

    [Theory]
    [InlineData("""{"type":"message.part.delta.streamed","properties":{"delta":"…"}}""")]
    [InlineData("""{"type":"message.updated","properties":{"info":"not a message"}}""")]
    [InlineData("""{"properties":{}}""")]
    public void Anything_else_is_nothing(string json)
        => RemoteMachineConnection.ToDomainEvent("s-1", Wire(json)).ShouldBeNull();

    private static JsonElement Wire(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
