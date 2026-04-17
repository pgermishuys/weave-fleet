using System.Text.Json;
using WeaveFleet.Application.Projections;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Tests.Projections;

public sealed class MessagePersistenceProjectionTests
{
    [Fact]
    public async Task Handle_delegatesToPersister_withSubjectMetadata()
    {
        var persister = new RecordingPersister();
        var sut = new MessagePersistenceProjection(persister);

        var evt = new HarnessEvent
        {
            Type = EventTypes.MessageCreated,
            SessionId = "oc-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { info = new { role = "assistant" } })
        };
        var ctx = new ProjectionContext(
            Tenant: "default",
            ProjectId: "proj",
            FleetSessionId: "sess",
            EventType: EventTypes.MessageCreated,
            UserId: "user-1",
            HarnessType: "opencode",
            StreamSequence: 42);

        await sut.HandleAsync(evt, ctx, CancellationToken.None);

        persister.Calls.ShouldHaveSingleItem();
        var call = persister.Calls[0];
        call.FleetSessionId.ShouldBe("sess");
        call.OwnerUserId.ShouldBe("user-1");
        call.Event.Type.ShouldBe(EventTypes.MessageCreated);
    }

    [Fact]
    public async Task Handle_skipsWhenUserIdIsMissing()
    {
        var persister = new RecordingPersister();
        var sut = new MessagePersistenceProjection(persister);

        var evt = new HarnessEvent { Type = EventTypes.MessageCreated, SessionId = "oc-1", Timestamp = DateTimeOffset.UtcNow };
        var ctx = new ProjectionContext("default", "proj", "sess", EventTypes.MessageCreated, null, "opencode", 1);

        await sut.HandleAsync(evt, ctx, CancellationToken.None);

        persister.Calls.ShouldBeEmpty();
    }

    private sealed class RecordingPersister : IHarnessEventPersister
    {
        public sealed record Call(string FleetSessionId, string OwnerUserId, HarnessEvent Event);
        public List<Call> Calls { get; } = new();

        public Task HandleAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt, CancellationToken ct)
        {
            Calls.Add(new Call(fleetSessionId, ownerUserId, evt));
            return Task.CompletedTask;
        }

        public void BufferTextDelta(string fleetSessionId, HarnessEvent evt) { }
        public Task FlushBufferedDeltasAsync(string fleetSessionId, string ownerUserId, CancellationToken ct) => Task.CompletedTask;
    }
}
