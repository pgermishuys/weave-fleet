using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Nats;

namespace WeaveFleet.Infrastructure.Tests.Nats;

public sealed class NatsNamingStrategyTests
{
    private readonly NatsNamingStrategy _sut = new(new NatsOptions(), nodeId: "node-A");

    [Fact]
    public void Subject_includesTenantProjectSessionAndType()
    {
        var subject = _sut.Subject(projectId: "proj-1", sessionId: "sess-1", eventType: "message.created");
        subject.ShouldBe("tenant.default.project.proj-1.session.sess-1.message.created");
    }

    [Fact]
    public void Subject_usesSameHierarchyForEphemeralEventTypes()
    {
        // Under the unified fan-out design, ephemeral and durable events share one subject tree.
        var subject = _sut.Subject(projectId: "proj-1", sessionId: "sess-1", eventType: "message.part.delta");
        subject.ShouldBe("tenant.default.project.proj-1.session.sess-1.message.part.delta");
    }

    [Fact]
    public void ScratchSentinel_appliedForMissingProjectId()
    {
        var subject = _sut.Subject(projectId: null, sessionId: "sess-1", eventType: "message.updated");
        subject.ShouldBe("tenant.default.project.scratch.session.sess-1.message.updated");
    }

    [Fact]
    public void FanOutSubscriptionFilter_coversAllSessions()
    {
        NatsNamingStrategy.FanOutSubscriptionFilter.ShouldBe("tenant.*.project.*.session.*.>");
    }

    [Fact]
    public void DurableStreamSubjects_enumerateDurableLeafTypesOnly()
    {
        var subjects = NatsNamingStrategy.DurableStreamSubjects;

        subjects.ShouldContain("tenant.*.project.*.session.*.message.created");
        subjects.ShouldContain("tenant.*.project.*.session.*.message.updated");
        subjects.ShouldContain("tenant.*.project.*.session.*.message.part.updated");
        subjects.ShouldContain("tenant.*.project.*.session.*.message.removed");
        subjects.ShouldContain("tenant.*.project.*.session.*.message.part.removed");
        subjects.ShouldContain("tenant.*.project.*.session.*.session.updated");
        subjects.ShouldContain("tenant.*.project.*.session.*.session.error");
        subjects.ShouldContain("tenant.*.project.*.session.*.session.compacted");
        subjects.ShouldContain("tenant.*.project.*.session.*.session.deleted");

        // Ephemerals must NOT be on the stream.
        subjects.ShouldNotContain("tenant.*.project.*.session.*.message.part.delta");
        subjects.ShouldNotContain("tenant.*.project.*.session.*.session.status");
        subjects.ShouldNotContain("tenant.*.project.*.session.*.session.idle");
    }

    [Fact]
    public void ParseSubject_extractsTenantProjectSessionAndEventType()
    {
        var parsed = NatsNamingStrategy.ParseSubject("tenant.default.project.proj-1.session.sess-1.message.part.updated");
        parsed.ShouldNotBeNull();
        parsed.Value.Tenant.ShouldBe("default");
        parsed.Value.ProjectId.ShouldBe("proj-1");
        parsed.Value.SessionId.ShouldBe("sess-1");
        parsed.Value.EventType.ShouldBe("message.part.updated");
    }

    [Fact]
    public void ParseSubject_returnsNullForMalformedInput()
    {
        NatsNamingStrategy.ParseSubject("garbage.subject").ShouldBeNull();
    }

    [Theory]
    [InlineData("proj.bad")]   // dot breaks the hierarchy
    [InlineData("proj*bad")]   // wildcard token
    [InlineData("proj>bad")]   // multi-wildcard token
    [InlineData("proj bad")]   // whitespace
    [InlineData("")]           // empty
    public void Subject_rejectsUnsafeSegmentCharacters(string badId)
    {
        Should.Throw<ArgumentException>(() =>
            _sut.Subject(projectId: badId, sessionId: "sess-1", eventType: "message.created"));
        Should.Throw<ArgumentException>(() =>
            _sut.Subject(projectId: "proj-1", sessionId: badId, eventType: "message.created"));
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
