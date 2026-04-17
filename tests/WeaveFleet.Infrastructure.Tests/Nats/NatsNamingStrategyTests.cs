using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Nats;

namespace WeaveFleet.Infrastructure.Tests.Nats;

public sealed class NatsNamingStrategyTests
{
    private readonly NatsNamingStrategy _sut = new(new NatsOptions(), nodeId: "node-A");

    [Fact]
    public void DurableSubject_includesTenantProjectSessionAndType()
    {
        var subject = _sut.DurableSubject(projectId: "proj-1", sessionId: "sess-1", eventType: "message.created");
        subject.ShouldBe("tenant.default.project.proj-1.session.sess-1.message.created");
    }

    [Fact]
    public void EphemeralSubject_usesLiveSegment()
    {
        var subject = _sut.EphemeralSubject(projectId: "proj-1", sessionId: "sess-1", eventType: "message.part.delta");
        subject.ShouldBe("tenant.default.project.proj-1.live.sess-1.message.part.delta");
    }

    [Fact]
    public void ScratchSentinel_appliedForMissingProjectId()
    {
        var subject = _sut.DurableSubject(projectId: null, sessionId: "sess-1", eventType: "message.updated");
        subject.ShouldBe("tenant.default.project.scratch.session.sess-1.message.updated");
    }

    [Fact]
    public void DurableStreamFilter_coversAllSessions()
    {
        NatsNamingStrategy.DurableStreamFilter.ShouldBe("tenant.*.project.*.session.*.>");
    }

    [Fact]
    public void EphemeralSubscriptionFilter_coversAllLiveSubjects()
    {
        NatsNamingStrategy.EphemeralSubscriptionFilter.ShouldBe("tenant.*.project.*.live.*.>");
    }

    [Fact]
    public void ParseDurableSubject_extractsTenantProjectSessionAndEventType()
    {
        var parsed = NatsNamingStrategy.ParseDurableSubject("tenant.default.project.proj-1.session.sess-1.message.part.updated");
        parsed.ShouldNotBeNull();
        parsed.Value.Tenant.ShouldBe("default");
        parsed.Value.ProjectId.ShouldBe("proj-1");
        parsed.Value.SessionId.ShouldBe("sess-1");
        parsed.Value.EventType.ShouldBe("message.part.updated");
    }

    [Fact]
    public void ParseDurableSubject_returnsNullForMalformedInput()
    {
        NatsNamingStrategy.ParseDurableSubject("garbage.subject").ShouldBeNull();
    }

    [Theory]
    [InlineData("proj.bad")]   // dot breaks the hierarchy
    [InlineData("proj*bad")]   // wildcard token
    [InlineData("proj>bad")]   // multi-wildcard token
    [InlineData("proj bad")]   // whitespace
    [InlineData("")]           // empty
    public void DurableSubject_rejectsUnsafeSegmentCharacters(string badId)
    {
        Should.Throw<ArgumentException>(() =>
            _sut.DurableSubject(projectId: badId, sessionId: "sess-1", eventType: "message.created"));
        Should.Throw<ArgumentException>(() =>
            _sut.DurableSubject(projectId: "proj-1", sessionId: badId, eventType: "message.created"));
    }

    [Fact]
    public void PerNodeConsumer_suffixesNodeId()
    {
        _sut.PerNodeConsumerName(projection: "ws-fanout").ShouldBe("fleet-sessions-ws-fanout-node-A");
    }

    [Fact]
    public void ClusterConsumer_hasNoNodeIdSuffix()
    {
        _sut.ClusterConsumerName(projection: "message-persistence").ShouldBe("fleet-sessions-message-persistence");
    }

    [Fact]
    public void Constructor_rejectsMissingNodeId()
    {
        Should.Throw<ArgumentException>(() => new NatsNamingStrategy(new NatsOptions(), nodeId: ""));
        Should.Throw<ArgumentException>(() => new NatsNamingStrategy(new NatsOptions(), nodeId: "  "));
    }
}
