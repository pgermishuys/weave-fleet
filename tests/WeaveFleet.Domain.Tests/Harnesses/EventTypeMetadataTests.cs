using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Domain.Tests.Harnesses;

public sealed class EventTypeMetadataTests
{
    // -----------------------------------------------------------------------
    // Durable events
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(EventTypes.MessageCreated)]
    [InlineData(EventTypes.MessageUpdated)]
    [InlineData(EventTypes.MessagePartUpdated)]
    [InlineData(EventTypes.MessageRemoved)]
    [InlineData(EventTypes.MessagePartRemoved)]
    [InlineData(EventTypes.SessionUpdated)]
    [InlineData(EventTypes.SessionError)]
    [InlineData(EventTypes.SessionCompacted)]
    [InlineData(EventTypes.SessionDeleted)]
    public void DurableEventTypes_AreClassifiedAsDurable(string eventType)
    {
        var classification = EventTypeMetadata.Classify(eventType);
        classification.IsDurable.ShouldBeTrue();
        classification.IsEphemeralRelay.ShouldBeFalse();
        classification.IsAdvisory.ShouldBeFalse();
    }

    // -----------------------------------------------------------------------
    // Ephemeral relay events
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(EventTypes.SessionStatus)]
    [InlineData(EventTypes.SessionIdle)]
    [InlineData(EventTypes.MessagePartDelta)]
    [InlineData(EventTypes.Error)]
    [InlineData("permission.request")]
    [InlineData("permission.denied")]
    public void EphemeralRelayEventTypes_AreClassifiedAsEphemeralRelay(string eventType)
    {
        var classification = EventTypeMetadata.Classify(eventType);
        classification.IsEphemeralRelay.ShouldBeTrue();
        classification.IsDurable.ShouldBeFalse();
        classification.IsAdvisory.ShouldBeTrue();
    }

    [Fact]
    public void session_created_is_classified_as_advisory_ephemeral_relay()
    {
        var classification = EventTypeMetadata.Classify(EventTypes.SessionCreated);

        classification.IsKnown.ShouldBeTrue();
        classification.IsEphemeralRelay.ShouldBeTrue();
        classification.IsDurable.ShouldBeFalse();
        classification.IsAdvisory.ShouldBeTrue();
    }

    // -----------------------------------------------------------------------
    // Reasoning filter events
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(EventTypes.MessageCreated)]
    [InlineData(EventTypes.MessageUpdated)]
    [InlineData(EventTypes.MessagePartUpdated)]
    public void MessageLifecycleEventTypes_RequireReasoningFilter(string eventType)
    {
        var classification = EventTypeMetadata.Classify(eventType);
        classification.RequiresReasoningFilter.ShouldBeTrue();
    }

    [Theory]
    [InlineData(EventTypes.MessageRemoved)]
    [InlineData(EventTypes.SessionUpdated)]
    [InlineData(EventTypes.SessionStatus)]
    [InlineData(EventTypes.Error)]
    public void NonMessageLifecycleEventTypes_DoNotRequireReasoningFilter(string eventType)
    {
        var classification = EventTypeMetadata.Classify(eventType);
        classification.RequiresReasoningFilter.ShouldBeFalse();
    }

    // -----------------------------------------------------------------------
    // Activity signal events
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(EventTypes.SessionStatus)]
    [InlineData(EventTypes.SessionIdle)]
    public void ActivitySignalEventTypes_AreClassifiedAsActivitySignals(string eventType)
    {
        var classification = EventTypeMetadata.Classify(eventType);
        classification.IsActivitySignal.ShouldBeTrue();
    }

    [Theory]
    [InlineData(EventTypes.MessageCreated)]
    [InlineData(EventTypes.MessagePartDelta)]
    [InlineData(EventTypes.Error)]
    public void NonActivityEventTypes_AreNotActivitySignals(string eventType)
    {
        var classification = EventTypeMetadata.Classify(eventType);
        classification.IsActivitySignal.ShouldBeFalse();
    }

    // -----------------------------------------------------------------------
    // Unknown event types
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("unknown.event")]
    [InlineData("")]
    [InlineData("some.future.event")]
    public void UnknownEventTypes_DefaultToNonDurableNonEphemeral(string eventType)
    {
        var classification = EventTypeMetadata.Classify(eventType);
        classification.IsKnown.ShouldBeFalse();
        classification.IsDurable.ShouldBeFalse();
        classification.IsEphemeralRelay.ShouldBeFalse();
        classification.IsAdvisory.ShouldBeFalse();
        classification.RequiresReasoningFilter.ShouldBeFalse();
        classification.IsActivitySignal.ShouldBeFalse();
    }

    // -----------------------------------------------------------------------
    // Server control / known-ignored events (not durable, not ephemeral relay)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(EventTypes.ServerHeartbeat)]
    [InlineData(EventTypes.ServerConnected)]
    [InlineData(EventTypes.SessionDiff)]
    public void KnownIgnoredEventTypes_AreKnownButNeitherDurableNorEphemeralRelay(string eventType)
    {
        var classification = EventTypeMetadata.Classify(eventType);
        classification.IsKnown.ShouldBeTrue();
        classification.IsDurable.ShouldBeFalse();
        classification.IsEphemeralRelay.ShouldBeFalse();
        classification.IsAdvisory.ShouldBeFalse();
    }

    // -----------------------------------------------------------------------
    // IsKnown — every classified type must set it
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(EventTypes.MessageCreated)]
    [InlineData(EventTypes.SessionStatus)]
    [InlineData(EventTypes.MessagePartDelta)]
    [InlineData("permission.request")]
    public void ClassifiedEventTypes_AreKnown(string eventType)
    {
        EventTypeMetadata.Classify(eventType).IsKnown.ShouldBeTrue();
    }
}
